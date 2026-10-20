using Simple.HttpServer.Alt;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml;

namespace Simple.HttpServer
{
    /// <summary>
    /// Settings holder for <see cref="SimpleHttpServer"/>. Loadable from an <c>app.config</c> custom config
    /// section, or constructed programmatically via <see cref="CreateDefault"/>.
    /// Example of section in application APP.CONFIG file:
    /// <example>
    /// 	<SimpleHttpServer enabled="True" port="8090" directory=".\HttpRoot\" connectQueueLength="200" sessionExpireTimeMin="1" >
    /// 	    <MimeTypes>
    /// 			XSL = text/xml
    /// 		</MimeTypes>
    /// 		<HttpReplyCodes>
    /// 			403 = Access denied
    /// 		</HttpReplyCodes>
    /// 		<HttpReplyExtraHeaders>
    /// 			Cache-Control: no-store, no-cache, must-revalidate, post-check=0, pre-check=0
    /// 			Pragma: no-cache
    /// 			Hash: $(MD5HASH)
    /// 		</HttpReplyExtraHeaders>
    /// 		<HttpReplyExtraHeaders.Static>
    /// 			Cache-Control: max-age=600
    /// 			Hash: $(MD5HASH)
    /// 		</HttpReplyExtraHeaders.Static>
    /// 	</SimpleHttpServer>
    /// </example>
    /// </summary>
    public class SimpleHttpServerSettings : IConfigurationSectionHandler
    {
        public static TraceSwitch TrcLvl { get { return SimpleHttpServer.TrcLvl; } }

        public static string CFG_SECTION_SimpleHttpServer = "SimpleHttpServer";

        public SimpleHttpServerSettings()
        {
            MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HttpReplyCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtraHttpReplyHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            seedDefaults();
        }

        public override string ToString()
        {
            return String.Format(
                "HttpSettings[{0}; ports={1}/{2}]: type={3}; qLen={4}, srvc={5}; dir={6};",
                (this.Enabled ? "On" : "Off"), this.Port, -1/*SSL*/, 
                this.EngineKind, 
                this.ConnectQueueLength,
                this.ServiceUrlPrefix, 
                this.Directory
                );
        }

        /// <summary>Whether the server should start when <see cref="SimpleHttpServer.Start"/> is called.</summary>
        public bool Enabled { get; set; }

        /// <summary>Selects the transport engine. See Simple.HttpServer.specs.md §2.1/§2.2.</summary>
        public HttpServerEngineKind EngineKind { get; set; }

        /// <summary>TCP port to listen on.</summary>
        public int Port { get; set; }

        /// <summary>Document root directory used for static file serving.</summary>
        public string Directory { get; set; }

        /// <summary>Backlog length for incoming connections.</summary>
        public int ConnectQueueLength { get; set; } = 200;

        /// <summary>URL prefix under which requests are routed to service handlers instead of static files.</summary>
        public string ServiceUrlPrefix { get; set; } = "/srvc";

        /// <summary>Page served when the requested URL is "/" or empty.</summary>
        public string DefaultPage { get; set; }

        /// <summary>Kept for request-session tracking; minutes after which an idle session expires.</summary>
        public int SessionExpireTimeMin { get; set; } = 10;
        public int SessionExpireIoCounter { get; set; } = -1;       

        /// <summary>
        /// If <see cref="HttpServerEngineKind.HttpListener"/> fails to start (e.g. HTTP.SYS unavailable), allow
        /// automatic fallback to <see cref="HttpServerEngineKind.Socket"/> instead of failing fast.
        /// </summary>
        public bool AutoFallbackToSocketEngine { get; set; }

        /// <summary>Extension (without leading dot) to content-type map used for static file serving.</summary>
        public Dictionary<string, string> MimeTypes { get; }

        /// <summary>Numeric status code to reason-phrase map.</summary>
        public Dictionary<string, string> HttpReplyCodes { get; }

        /// <summary>Extra headers added to every response.</summary>
        public Dictionary<string, string> ExtraHttpReplyHeaders { get; }

        public XmlElement ConfigNodeRef = null;

        public delegate bool ResolveConfigValueMethod(string pPrmName, ref string pPrmValue);
        public static ResolveConfigValueMethod ConfigValueResolver = null;


        public string ActualDirectory
        {
            get
            {
                string result = this.Directory;
                if (result.Length >= 2 && result[0] == '.' && (result[1] == Path.DirectorySeparatorChar || result[1] == Path.AltDirectorySeparatorChar))
                {
                    result = result.Remove(0, 1).Insert(0, Utils.ApplicationHomePath);
                    result = Path.GetFullPath(result);
                }
                return result;
            }
        }

        /// <summary>IConfigurationSectionHandler implementation: builds a <see cref="SimpleHttpServerSettings"/> from an app.config XML node.</summary>
        public object Create(object parent, object configContext, XmlNode section)
        {
            var element = section as XmlElement;
            if (element == null) return this;

            this.ConfigNodeRef = element;

            Dictionary<string, string> props = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            Utils.LoadAttributes(props, element, true);

            string s;
            if (ConfigValueResolver != null)
            {
                Dictionary<string, string> resolvedProps = new Dictionary<string, string>();
                foreach (KeyValuePair<string, string> it in props)
                {
                    s = it.Value;
                    if (ConfigValueResolver(it.Key, ref s))
                        resolvedProps[it.Key] = s;
                }
                if (resolvedProps.Count > 0)
                    Utils.Merge(props, resolvedProps, true);
            }

            string id;

            id = "enabled"; if (props.ContainsKey(id)) this.Enabled = parseBool(props[id], this.Enabled);
            id = "engine"; if (props.ContainsKey(id)) this.EngineKind = parseEngine(props[id]);
            id = "port"; if (props.ContainsKey(id)) this.Port = parseInt(props[id], this.Port);
            id = "directory"; if (props.ContainsKey(id)) this.Directory = props[id];
            id = "connectQueueLength"; if (props.ContainsKey(id)) this.ConnectQueueLength = parseInt(props[id], this.ConnectQueueLength);
            id = "serviceUrlPrefix"; if (props.ContainsKey(id)) this.ServiceUrlPrefix = props[id];
            id = "defaultPage"; if (props.ContainsKey(id)) this.DefaultPage = props[id];
            id = "sessionExpireTimeMin"; if (props.ContainsKey(id)) this.SessionExpireTimeMin = parseInt(props[id], this.SessionExpireTimeMin);
            id = "sessionExpireIoCounter"; if (props.ContainsKey(id)) this.SessionExpireIoCounter = parseInt(props[id], this.SessionExpireIoCounter);
            id = "autoFallbackToSocketEngine"; if (props.ContainsKey(id)) this.AutoFallbackToSocketEngine = parseBool(props[id], this.AutoFallbackToSocketEngine);

            foreach (XmlNode node in element.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;

                if (string.Equals(node.Name, "MimeTypes", StringComparison.OrdinalIgnoreCase))
                    parseKeyValueLines(node.InnerText, MimeTypes);
                else if (string.Equals(node.Name, "HttpReplyCodes", StringComparison.OrdinalIgnoreCase))
                    parseKeyValueLines(node.InnerText, HttpReplyCodes);
                else if (string.Equals(node.Name, "ExtraHttpReplyHeaders", StringComparison.OrdinalIgnoreCase))
                    parseKeyValueLines(node.InnerText, ExtraHttpReplyHeaders);
            }

            return this;
        }

        /// <summary>
        /// Validate if specified filename (with full or relative path) is reside in configured HttpRoot directory.
        /// Also it returns full actual path to referenced file.
        /// </summary>
        /// <param name="pFilename">Full or relative path to file to check</param>
        /// <param name="pActualFilename">Full actual path to referenced file</param>
        /// <returns>Returns true when referenced file resides in configured HttpRoot directory</returns>
        public bool IsValidHttpRoot(string pFilename, out string pActualFilename)
        {
            string actualFilename = pFilename;
            string root = Path.GetPathRoot(actualFilename);
            if (string.IsNullOrEmpty(root) || (root.Length > 0 && (root[0] == Path.DirectorySeparatorChar || root[1] == Path.AltDirectorySeparatorChar)))
                actualFilename = Utils.IncludeTrailingSlash(Directory) + actualFilename;
            actualFilename = Path.GetFullPath(actualFilename);
            pActualFilename = actualFilename;
            string actualPath = Utils.ExcludeTrailingSlash(Path.GetDirectoryName(actualFilename));
            string d1 = actualPath.ToLower().Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string d2 = Utils.ExcludeTrailingSlash(this.ActualDirectory).ToLower().Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool isOk = d1.StartsWith(d2);
            Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceInfo, SimpleHttpServer.TrcLvl.TraceInfo ? String.Format(
                "? HttpSrvSet:IsValidRoot( {0} => {1} ): {2} => {3}", pFilename, pActualFilename,
                (isOk ? "valid" : "In-Valid!"), (isOk && File.Exists(pActualFilename) ? "ok" : "not-found!")) : "");
            return isOk;
        }

        #region Static API

        /// <summary>Creates settings with sane defaults, used when no app.config section is present.</summary>
        public static SimpleHttpServerSettings CreateDefault()
        {
            return new SimpleHttpServerSettings
            {
                Enabled = true,
                EngineKind = HttpServerEngineKind.Auto,
                Port = 8080,
                Directory = ".\\httproot\\",
                ConnectQueueLength = 200,
                ServiceUrlPrefix = "/srvc",
                DefaultPage = "/index.html",
                SessionExpireTimeMin = 5,
                AutoFallbackToSocketEngine = false
            };
        }

        /// <summary>Loads settings from the named app.config section, falling back to <see cref="CreateDefault"/> if absent.</summary>
        public static SimpleHttpServerSettings LoadFromConfig(string pConfigSectionName)
        {
            if (string.IsNullOrEmpty(pConfigSectionName))
                pConfigSectionName = CFG_SECTION_SimpleHttpServer;

            SimpleHttpServerSettings settings;
            try
            {
                settings = ConfigurationManager.GetSection(pConfigSectionName) as SimpleHttpServerSettings;

                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceWarning, SimpleHttpServer.TrcLvl.TraceWarning ? String.Format(
                    "Http.CfgLoad => {0}", settings) : "");
            }
            catch (ConfigurationErrorsException ex)
            {
                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceWarning, SimpleHttpServer.TrcLvl.TraceWarning ? String.Format(
                    "Http.CfgLoad.{0}", Utils.FormatErrorMsg(ex)) : "");
                
                settings = CreateDefault();

                Trace.WriteLineIf(SimpleHttpServer.TrcLvl.TraceWarning, SimpleHttpServer.TrcLvl.TraceWarning ? String.Format(
                    "Http.CfgDefault => {0}", settings) : "");
            }
            return settings;
        }

        #endregion // Static API

        #region Implementation details

        private void seedDefaults()
        {
            foreach (var kv in DefaultMimeTypes) MimeTypes[kv.Key] = kv.Value;
            foreach (var kv in DefaultHttpReplyCodes) HttpReplyCodes[kv.Key] = kv.Value;
        }

        private static void parseKeyValueLines(string pText, Dictionary<string, string> pTarget)
        {
            if (string.IsNullOrWhiteSpace(pText)) return;
            string[] lines = pText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                pTarget[key] = value;
            }
        }

        private static HttpServerEngineKind parseEngine(string pValue)
        {
            return Enum.TryParse(pValue, true, out HttpServerEngineKind kind) ? kind : HttpServerEngineKind.Auto;
        }

        private static bool parseBool(string pValue, bool pDefault)
        {
            return bool.TryParse(pValue, out bool result) ? result : pDefault;
        }

        private static int parseInt(string pValue, int pDefault)
        {
            return int.TryParse(pValue, out int result) ? result : pDefault;
        }

        private static readonly Dictionary<string, string> DefaultMimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gif"] = "image/gif",
            ["jpeg"] = "image/jpeg",
            ["jpg"] = "image/jpeg",
            ["png"] = "image/png",
            ["bmp"] = "image/bmp",
            ["ico"] = "image/vnd.microsoft.icon",
            ["svg"] = "image/svg+xml",
            ["html"] = "text/html",
            ["htm"] = "text/html",
            ["xml"] = "text/xml",
            ["xsl"] = "text/xml",
            ["xslt"] = "text/xml",
            ["css"] = "text/css",
            ["js"] = "text/javascript",
            ["txt"] = "text/plain",
            ["log"] = "text/plain",
            ["json"] = "application/json"
        };

        private static readonly Dictionary<string, string> DefaultHttpReplyCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["200"] = "OK",
            ["301"] = "Moved Permanently",
            ["302"] = "Found",
            ["400"] = "Bad Request",
            ["401"] = "Unauthorized",
            ["403"] = "Forbidden",
            ["404"] = "Not Found",
            ["405"] = "Method Not Allowed",
            ["500"] = "Internal Server Error",
            ["503"] = "Service Unavailable"
        };


        #endregion // Implementation details
    }
}
