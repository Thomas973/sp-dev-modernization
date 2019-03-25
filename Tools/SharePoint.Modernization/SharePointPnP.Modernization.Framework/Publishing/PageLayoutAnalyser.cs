﻿using Microsoft.SharePoint.Client;
using SharePointPnP.Modernization.Framework.Entities;
using SharePointPnP.Modernization.Framework.Telemetry;
using SharePointPnP.Modernization.Framework.Transform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;

using ContentType = Microsoft.SharePoint.Client.ContentType;
using File = Microsoft.SharePoint.Client.File;

namespace SharePointPnP.Modernization.Framework.Publishing
{
    public class PageLayoutAnalyser : BaseTransform
    {
        /*
         * Plan
         *  Read a publishing page or read all the publishing page layouts - need to consider both options
         *  Validate that the client context is a publishing site
         *  Determine page layouts and the associated content type
         *  - Using web part manager scan for web part zones and pre-populated web parts
         *  - Detect for field controls - only the metadata behind these can be transformed without an SPFX web part
         *      - Metadata mapping to web part - only some types will be supported
         *  - Using HTML parser deep analysis of the file to map out detected web parts. These are fixed point in the publishing layout.
         *      - This same method could be used to parse HTML fields for inline web parts
         *  - Generate a layout mapping based on analysis
         *  - Validate the Xml prior to output
         *  - Split into molecules of operation for unit testing
         *  - Detect grid system, table or fabric for layout options, needs to be extensible - consider...
         *  
         */

        
        private ClientContext _siteCollContext;
        private ClientContext _sourceContext;
        
        private PublishingPageTransformation _mapping;
        private string _defaultFileName = "PageLayoutMapping.xml";

        const string AvailablePageLayouts = "__PageLayouts";
        const string DefaultPageLayout = "__DefaultPageLayout";
        const string FileRefField = "FileRef";
        const string FileLeafRefField = "FileLeafRef";
        const string PublishingAssociatedContentType = "PublishingAssociatedContentType";
        const string PublishingPageLayoutField = "PublishingPageLayout";
        const string PageLayoutBaseContentTypeId = "0x01010007FF3E057FA8AB4AA42FCB67B453FFC1"; //Page Layout Content Type Id

        private HtmlParser parser;

        /// <summary>
        /// Analyse Page Layouts class constructor
        /// </summary>
        /// <param name="sourceContext">This should be the context of the source web</param>
        /// <param name="logObservers">List of log observers</param>
        public PageLayoutAnalyser(ClientContext sourceContext, IList<ILogObserver> logObservers = null)
        {
            // Register observers
            if (logObservers != null){
                foreach (var observer in logObservers){
                    base.RegisterObserver(observer);
                }
            }

            _mapping = new PublishingPageTransformation();

            _sourceContext = sourceContext;
            EnsureSiteCollectionContext(sourceContext);
            parser = new HtmlParser(new HtmlParserOptions() { IsEmbedded = true }, Configuration.Default.WithDefaultLoader().WithCss());
        }


        /// <summary>
        /// Main entry point into the class to analyse the page layouts
        /// </summary>
        public void Analyse()
        {
            // Determine if ‘default’ layouts for the OOB page layouts
            // When there’s no layout we “generate” a best effort one and store it in cache.Generation can 
            //  be done by looking at the field types and inspecting the layout aspx file. This same generation 
            //  part can be used in point 2 for customers to generate a starting layout mapping file which they then can edit
            // Don't assume that you are in a top level site, you maybe in a sub site

            if (Validate())
            {
                var spPageLayouts = GetPageLayouts();
                List<PageLayout> pageLayoutMappings = new List<PageLayout>();

                foreach(var layout in spPageLayouts)
                {
                    

                    string assocContentType = layout[PublishingAssociatedContentType].ToString();
                    var assocContentTypeParts = assocContentType.Split(new string[] { ";#" }, StringSplitOptions.RemoveEmptyEntries);

                    var metadata = GetMetadatafromPageLayoutAssociatedContentType(assocContentTypeParts[1]);
                    var webParts = ExtractFieldControlsFromPageLayoutHtml(layout);
                    var zones = ExtractWebPartZonesFromPageLayoutHtml(layout);

                    pageLayoutMappings.Add(new PageLayout()
                    {
                        Name = layout.DisplayName,
                        AssociatedContentType = assocContentTypeParts?[0],
                        MetaData = metadata,
                        WebParts = webParts,
                        WebPartZones = zones

                    });

                }

                //Add to mapping
                _mapping.PageLayouts = pageLayoutMappings.ToArray();

            }


        }

        /// <summary>
        /// Perform validation to ensure the source site contains page layouts
        /// </summary>
        public bool Validate()
        {
            if (_sourceContext.Web.IsPublishingWeb())
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ensures that we have context of the source site collection
        /// </summary>
        /// <param name="context"></param>
        public void EnsureSiteCollectionContext(ClientContext context)
        {
            try
            {
                if (context.Web.IsSubSite())
                {
                    string siteCollectionUrl = context.Site.EnsureProperty(o => o.Url);
                    _siteCollContext = context.Clone(siteCollectionUrl);
                }
                else
                {
                    _siteCollContext = context;
                }
            }
            catch (Exception ex)
            {
                LogError(LogStrings.Error_CannotGetSiteCollContext, LogStrings.Heading_PageLayoutAnalyser, ex);
            }
        }

        /// <summary>
        /// Determines the page layouts in the current web
        /// </summary>
        public ListItemCollection GetPageLayouts()
        {
            var availablePageLayouts = GetPropertyBagValue<string>(_siteCollContext.Web, AvailablePageLayouts, "");
            // If empty then gather all

            var masterPageGallery = _siteCollContext.Web.GetCatalog((int)ListTemplateType.MasterPageCatalog);
            _siteCollContext.Load(masterPageGallery, x => x.RootFolder.ServerRelativeUrl);
            _siteCollContext.ExecuteQueryRetry();

            var query = new CamlQuery();
            // Use query Scope='RecursiveAll' to iterate through sub folders of Master page library because we might have file in folder hierarchy
            // Ensure that we are getting layouts with at least one published version, not hidden layouts
            query.ViewXml =
                $"<View Scope='RecursiveAll'>" +
                    $"<Query>" +
                        $"<Where>" +
                            $"<And>" +
                                $"<And>" +
                                    $"<Geq>" +
                                        $"<FieldRef Name='_UIVersionString'/><Value Type='Text'>1.0</Value>" +
                                    $"</Geq>" +
                                    $"<BeginsWith>" +
                                        $"<FieldRef Name='ContentTypeId'/><Value Type='ContentTypeId'>{PageLayoutBaseContentTypeId}</Value>" +
                                    $"</BeginsWith>" +
                                $"</And>" +
                                $"<Or>"+
                                    $"<Eq>" +
                                        $"<FieldRef Name='PublishingHidden'/><Value Type='Boolean'>0</Value>" +
                                    $"</Eq>" +
                                    $"<IsNull>" +
                                        $"<FieldRef Name='PublishingHidden'/>" +
                                    $"</IsNull>" +
                                $"</Or>" +
                            $"</And>" +
                         $"</Where>" +
                    $"</Query>" +
                    $"<ViewFields>" +
                        $"<FieldRef Name='"+ PublishingAssociatedContentType + $"' />" +
                        $"<FieldRef Name='PublishingHidden' />" +
                        $"<FieldRef Name='Title' />" +
                    $"</ViewFields>" +
                  $"</View>";

            var galleryItems = masterPageGallery.GetItems(query);
            _siteCollContext.Load(masterPageGallery);
            _siteCollContext.Load(galleryItems);
            _siteCollContext.Load(galleryItems, i => i.Include(o=>o.DisplayName), 
                i => i.Include(o => o.File),
                i => i.Include(o => o.File.ServerRelativeUrl));

            _siteCollContext.ExecuteQueryRetry();
            
            return galleryItems.Count > 0 ? galleryItems : null;

        }

        /// <summary>
        /// Gets the page layout for analysis
        /// </summary>
        public WebPartField[] GetPageLayoutFileWebParts(ListItem pageLayout)
        {

            List<WebPartField> wpFields = new List<WebPartField>();
            
            File file = pageLayout.File;
            var webPartManager = file.GetLimitedWebPartManager(Microsoft.SharePoint.Client.WebParts.PersonalizationScope.Shared);
            
            _siteCollContext.Load(webPartManager);
            _siteCollContext.Load(webPartManager.WebParts);
            _siteCollContext.Load(webPartManager.WebParts, 
                i=>i.Include(o=>o.WebPart.Title),
                i => i.Include(o => o.ZoneId),
                i => i.Include(o => o.WebPart));
            _siteCollContext.Load(file);
            _siteCollContext.ExecuteQueryRetry();

            var wps = webPartManager.WebParts;

            foreach(var part in wps){

                var props = part.WebPart.Properties.FieldValues;
                List<WebPartProperty> partProperties = new List<WebPartProperty>();

                foreach(var prop in props)
                {
                    partProperties.Add(new WebPartProperty() { Name = prop.Key, Type = WebPartProperyType.@string });
                }
                
                wpFields.Add(new WebPartField()
                {
                    Name = part.WebPart.Title,
                    Property = partProperties.ToArray()

                });

            }

            return wpFields.ToArray();
        }


        /// <summary>
        /// Determine the page layout from a publishing page
        /// </summary>
        public void GetPageLayoutFromPublishingPage()
        {
            //Note: ListItemExtensions class contains this logic - reuse.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get Metadata mapping from the page layout associated content type
        /// </summary>
        /// <param name="contentTypeId">Id of the content type</param>
        public MetaDataField[] GetMetadatafromPageLayoutAssociatedContentType(string contentTypeId)
        {
            List<MetaDataField> fields = new List<MetaDataField>();

            try
            {

                if (_siteCollContext.Web.ContentTypeExistsById(contentTypeId, true))
                {
                    var cType = _siteCollContext.Web.ContentTypes.GetById(contentTypeId);

                    var spFields = cType.EnsureProperty(o => o.Fields);

                    foreach (var fld in spFields.Where(o => o.Hidden == false))
                    {
                        fields.Add(new MetaDataField()
                        {
                            Name = fld.InternalName,
                            Functions = "",
                            TargetFieldName = ""
                        });
                    }
                }

            }catch(Exception ex)
            {
                LogError(LogStrings.Error_CannotMapMetadataFields, LogStrings.Heading_PageLayoutAnalyser, ex);
            }

            return fields.ToArray();
        }


        /// <summary>
        /// Get fixed web parts defined in the page layout
        /// </summary>
        public void GetFixedWebPartsFromZones()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// This method analyses the Html strcuture to determine layout
        /// </summary>
        public void ExtractLayoutFromHtmlStructure()
        {
            /*Plan
             * Scan through the file to plot the 
             * - Determine if a grid system, classic, fabric or Html structure is in use
             * - Work out the location of the web part in relation to the grid system
            */
        }


        /// <summary>
        /// Extract the web parts from the page layout HTML outside of web part zones
        /// </summary>
        public WebPartField[] ExtractFieldControlsFromPageLayoutHtml(ListItem pageLayout)
        {
            /*Plan
             * Scan through the file to find the web parts by the tags
             * Extract and convert to definition 
            */

            List<WebPartField> webParts = new List<WebPartField>();

            pageLayout.EnsureProperties(o => o.File, o => o.File.ServerRelativeUrl);
            var fileUrl = pageLayout.File.ServerRelativeUrl;

            var fileHtml = _siteCollContext.Web.GetFileAsString(fileUrl);

            using (var document = this.parser.Parse(fileHtml))
            {
                //TODO: Add further processing to find if the tags are in a grid system
                //TODO: Remove unnecessary controls
                //TODO: DeDup - Some controls can be inside an edit panel

                var fieldControls = document.All.Where(o => o.TagName.Contains("SHAREPOINTWEBCONTROLS"));

                foreach (var control in fieldControls)
                {
                    var attributes = control.Attributes;
                    var fieldName = "";
                    if (attributes.Any(o => o.Name == "fieldname")) {

                        fieldName = attributes["fieldname"].Value;
                    }

                    webParts.Add(new WebPartField()
                    {
                        Name = fieldName,
                        TargetWebPart = "",
                        Row = 0,
                        Column = 0
                    });
                }

            }

            return webParts.ToArray();

        }

        /// <summary>
        /// Extract the web parts from the page layout HTML outside of web part zones
        /// </summary>
        public WebPartZone[] ExtractWebPartZonesFromPageLayoutHtml(ListItem pageLayout)
        {
            /*Plan
             * Scan through the file to find the web parts by the tags
             * Extract and convert to definition 
            */
            List<WebPartZone> zones = new List<WebPartZone>();

            pageLayout.EnsureProperties(o => o.File, o => o.File.ServerRelativeUrl);
            var fileUrl = pageLayout.File.ServerRelativeUrl;

            var fileHtml = _siteCollContext.Web.GetFileAsString(fileUrl);

            using (var document = this.parser.Parse(fileHtml))
            {
                //TODO: Add further processing to find if the tags are in a grid system

                var webPartZones = document.All.Where(o => o.TagName.Contains("WEBPARTZONE"));

                foreach(var webPartZone in webPartZones)
                {
                    zones.Add(new WebPartZone()
                    {
                        ZoneId = webPartZone.Id,
                        Column = 0,
                        Row = 0,
                        ZoneIndex = 0
                    });
                }

            }

            return zones.ToArray();

        }

        /// <summary>
        /// Generate the mapping file to output from the analysis
        /// </summary>
        public string GenerateMappingFile()
        {
            try
            {
                XmlSerializer xmlMapping = new XmlSerializer(typeof(PublishingPageTransformation));
                
                var mappingFileName = _defaultFileName;

                using (StreamWriter sw = new StreamWriter(mappingFileName, false))
                {
                    xmlMapping.Serialize(sw, _mapping);
                }

                var xmlMappingFileLocation = $"{ Environment.CurrentDirectory }\\{ mappingFileName}";
                LogInfo($"{LogStrings.XmlMappingSavedAs}: {xmlMappingFileLocation}");

                return xmlMappingFileLocation;

            }catch(Exception ex)
            {
                var message = string.Format(LogStrings.Error_CannotWriteToXmlFile, ex.Message, ex.StackTrace);
                Console.WriteLine(message);
                LogError(message, LogStrings.Heading_PageLayoutAnalyser, ex);
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets property bag value
        /// </summary>
        /// <typeparam name="T">Cast to type of</typeparam>
        /// <param name="web">Current Web</param>
        /// <param name="key">KeyValue Pair - Key</param>
        /// <param name="defaultValue">Default Value</param>
        /// <returns></returns>
        private static T GetPropertyBagValue<T>(Web web, string key, T defaultValue)
        {
            //TODO: Add to helpers class - source from Publishing Analyser

            web.EnsureProperties(p => p.AllProperties);

            if (web.AllProperties.FieldValues.ContainsKey(key))
            {
                return (T)web.AllProperties.FieldValues[key];
            }
            else
            {
                return defaultValue;
            }
        }
    }
}