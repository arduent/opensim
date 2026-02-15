using System;
using System.Collections;
using System.Text.Json;
using Mono.Addins;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Groups
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MatrixGroupInjectModule")]
    public class MatrixGroupInjectModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(typeof(MatrixGroupInjectModule));

        private bool m_enabled;
        private string m_secret = string.Empty;

        // One instance per region server process; we use the Scene we were added to
        private Scene m_scene;

        public string Name => "MatrixGroupInjectModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var cfg = source.Configs["MatrixBridge"];
            if (cfg == null)
                return;

            m_enabled = cfg.GetBoolean("Enabled", false);
            m_secret = cfg.GetString("SharedSecret", string.Empty);

            if (m_enabled)
	    {
                m_log.Info("[MatrixBridge] Region module enabled");
            } else {
                m_log.Info("[MatrixBridge] Region module NOT enabled - missing config");
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;

            MainServer.Instance.AddHTTPHandler(
                "/matrix/group-message",
                HandleMatrixGroupMessage);

            m_log.Info("[MatrixBridge] Region HTTP endpoint registered at /matrix/group-message");
        }

        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
        public void PostInitialise() { }
        public void Close() { }

        private Hashtable HandleMatrixGroupMessage(Hashtable request)
        {
            var response = new Hashtable();

            m_log.Info("[MatrixBridge] Initiate HandleMatrixGroupMessage.");

            try
            {
                // --- Auth header ---
                if (!request.ContainsKey("headers") || request["headers"] is not Hashtable headers)
                    return Error(response, 400, "Missing headers");

                string secretHeader = null;
                
                foreach (DictionaryEntry entry in headers)
                {
                    if (entry.Key.ToString().Equals("X-Bridge-Secret", StringComparison.OrdinalIgnoreCase))
                    {
                        secretHeader = entry.Value?.ToString();
                        break;
                    }
                }

                //m_log.InfoFormat("[MatrixBridge] Secret Header {0} :: Config Value {1}", secretHeader, m_secret);
                
                if (string.IsNullOrEmpty(secretHeader) || !CryptographicEquals(secretHeader, m_secret))
                {
                    m_log.Info("[MatrixBridge] 401 Unauthorized Request.");
                    return Error(response, 401, "Unauthorized");
                }
                
                // --- Body ---
                var body = ExtractBodyAsString(request);
                if (string.IsNullOrWhiteSpace(body))
                {
                    m_log.Info("[MatrixBridge] 400 Empty Body...");
                    return Error(response, 400, "Empty body");
                }

                // --- Parse JSON ---
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("group_uuid", out var groupEl) ||
                    !root.TryGetProperty("from_name", out var fromEl) ||
                    !root.TryGetProperty("message", out var msgEl))
                {
                    m_log.Info("[MatrixBridge] 400 Missing fields...");
                    return Error(response, 400, "Missing fields");
                }

                var groupStr = groupEl.GetString() ?? string.Empty;
                var fromName = fromEl.GetString() ?? string.Empty;
                var message  = msgEl.GetString() ?? string.Empty;

                if (!UUID.TryParse(groupStr, out UUID groupID))
                {
                    m_log.Info("[MatrixBridge] 400 Invalid Group UUID");
                    return Error(response, 400, "Invalid group UUID");
                }

                if (string.IsNullOrWhiteSpace(message))
                {
                    m_log.Info("[MatrixBridge] 400 Empty Message...");
                    return Error(response, 400, "Empty message");
                }

                InjectToGroup(groupID, fromName, message);

                response["int_response_code"] = 200;
                response["content_type"] = "application/json";
                response["str_response_string"] = "{\"ok\":true}";
                return response;
            }
            catch (Exception e)
            {
                m_log.Error("[MatrixBridge] 500 Injection exception", e);
                return Error(response, 500, "Internal error");
            }
        }

        private void InjectToGroup(UUID groupID, string fromName, string message)
        {
            if (m_scene == null)
            {
                m_log.Warn("[MatrixBridge] No scene available");
                return;
            }

            var groupsModule = m_scene.RequestModuleInterface<IGroupsMessagingModule>();
            if (groupsModule == null)
            {
                m_log.Warn("[MatrixBridge] IGroupsMessagingModule not found in scene");
                return;
            }

            var im = new GridInstantMessage
            {
                fromAgentID = UUID.Zero.Guid,
                fromAgentName = "[Matrix] " + (string.IsNullOrWhiteSpace(fromName) ? "unknown" : fromName),
                dialog = (byte)InstantMessageDialog.SessionSend,
                imSessionID = groupID.Guid,
                message = message,
                fromGroup = true,
                offline = 0,
                timestamp = (uint)Util.UnixTimeSinceEpoch(),
                binaryBucket = Util.StringToBytes256("Matrix Bridge")
            };

            // This is the correct injection point (region-side group broadcaster)
            groupsModule.SendMessageToGroup(im, groupID);

            m_log.InfoFormat("[MatrixBridge] Injected message into group {0} from {1}", groupID, fromName);
        }

        private static string ExtractBodyAsString(Hashtable request)
        {
            // Depending on handler internals, body may arrive as string or byte[]
            if (request == null)
                return string.Empty;
        
            if (!request.ContainsKey("body") || request["body"] == null)
                return string.Empty;
        
            var bodyObj = request["body"];
        
            if (bodyObj is string s)
                return s;
        
            if (bodyObj is byte[] b)
                return System.Text.Encoding.UTF8.GetString(b);
        
            return bodyObj.ToString();
        }
        
        private static Hashtable Error(Hashtable response, int code, string msg)
        {
            response["int_response_code"] = code;
            response["content_type"] = "application/json";
            response["str_response_string"] = "{\"error\":\"" + msg.Replace("\"", "'") + "\"}";
            return response;
        }

        // constant-time compare to avoid leaking token length/prefix
        private static bool CryptographicEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            var result = 0;
            for (int i = 0; i < a.Length; i++)
                result |= a[i] ^ b[i];

            return result == 0;
        }
    }
}

