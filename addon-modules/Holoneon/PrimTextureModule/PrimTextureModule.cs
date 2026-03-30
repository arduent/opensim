// PrimTextureModule.cs
// OpenSim 0.9.3.1 (.NET 8) region module
//
// Provides a lightweight HTTP endpoint on each region server returning
// texture UUIDs for a prim by UUID, as JSON.
//
// Endpoint:
//   http://<regionserver>:9100/primtextures?uuid=<prim-uuid>
//   http://<regionserver>:9100/primtextures?uuid=<prim-uuid>&region=<RegionName>
//
// Response:
//   {
//     "prim_uuid": "...",
//     "prim_name": "...",
//     "region": "...",
//     "faces": [
//       { "face": 0, "diffuse": "uuid", "normal": "uuid", "specular": "uuid" },
//       ...
//     ]
//   }
//
// Config (OpenSim.ini):
//   [Modules]
//   PrimTextureModule = PrimTextureModule
//
//   [PrimTexture]
//   Enabled = true
//   ListenPort = 9100
//   ListenAddress = 0.0.0.0
//
// PHP usage:
//   $data = json_decode(file_get_contents("http://region1:9100/primtextures?uuid=xxxx"), true);
//
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;

using log4net;
using Nini.Config;
using Mono.Addins;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenMetaverse;

[assembly: Addin("PrimTextureModule", "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("OpenSim Addin for PrimTexture")]
[assembly: AddinAuthor("Fiona Sweet fiona@pobox.holoneon.com")]

namespace OpenSim.Region.OptionalModules.Holoneon.PrimTexture
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class PrimTextureModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_enabled = false;
        private int m_listenPort = 9100;
        private string m_listenAddress = "0.0.0.0";

        private readonly List<Scene> m_scenes = new();
        private HttpListener m_listener;
        private Thread m_listenerThread;
        private bool m_running = false;

        public string Name => "PrimTextureModule";
        public Type ReplaceableInterface => null;

        // ----------------------------------------------------------------
        // ISharedRegionModule
        // ----------------------------------------------------------------

        public void Initialise(IConfigSource source)
        {
            IConfig modules = source.Configs["Modules"];
            if (modules == null) return;

            string which = modules.GetString("PrimTextureModule", string.Empty);
            if (!string.Equals(which, Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(which, "PrimTextureModule", StringComparison.OrdinalIgnoreCase))
                return;

            IConfig cfg = source.Configs["PrimTexture"];
            if (cfg != null)
            {
                m_enabled       = cfg.GetBoolean("Enabled", true);
                m_listenPort    = cfg.GetInt("ListenPort", 9100);
                m_listenAddress = cfg.GetString("ListenAddress", "0.0.0.0");
            }
            else
            {
                m_enabled = true;
            }

            if (m_enabled)
                m_log.InfoFormat("[PRIM TEXTURE] Initialised. Will listen on {0}:{1}",
                    m_listenAddress, m_listenPort);
            else
                m_log.Info("[PRIM TEXTURE] Disabled in config.");
        }

        public void PostInitialise()
        {
            if (!m_enabled) return;
            StartListener();
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            lock (m_scenes)
                if (!m_scenes.Contains(scene))
                    m_scenes.Add(scene);

            m_log.InfoFormat("[PRIM TEXTURE] Region added: {0}", scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            lock (m_scenes)
                m_scenes.Remove(scene);
        }

        public void RegionLoaded(Scene scene) { }

        public void Close()
        {
            m_running = false;
            try { m_listener?.Stop(); } catch { }
            m_log.Info("[PRIM TEXTURE] Stopped.");
        }

        // ----------------------------------------------------------------
        // HTTP Listener
        // ----------------------------------------------------------------

        private void StartListener()
        {
            try
            {
                m_listener = new HttpListener();

                // HttpListener needs a + or specific IP but not 0.0.0.0
                string prefix = m_listenAddress == "0.0.0.0"
                    ? $"http://+:{m_listenPort}/primtextures/"
                    : $"http://{m_listenAddress}:{m_listenPort}/primtextures/";

                m_listener.Prefixes.Add(prefix);
                m_listener.Start();
                m_running = true;

                m_listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "PrimTextureListener"
                };
                m_listenerThread.Start();

                m_log.InfoFormat("[PRIM TEXTURE] Listening on {0}", prefix);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[PRIM TEXTURE] Failed to start listener: {0}", ex.Message);
                m_log.Error("[PRIM TEXTURE] If using 0.0.0.0, you may need to run: " +
                    $"netsh http add urlacl url=http://+:{m_listenPort}/primtextures/ user=Everyone  (Windows)" +
                    $"  OR ensure mono has permission (Linux).");
            }
        }

        private void ListenLoop()
        {
            while (m_running)
            {
                try
                {
                    HttpListenerContext ctx = m_listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException)
                {
                    // Listener stopped
                    break;
                }
                catch (Exception ex)
                {
                    if (m_running)
                        m_log.WarnFormat("[PRIM TEXTURE] Listener error: {0}", ex.Message);
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            string responseJson;
            int statusCode = 200;

            try
            {
                // Parse query string
                string primUuidStr  = ctx.Request.QueryString["uuid"]   ?? string.Empty;
                string regionFilter = ctx.Request.QueryString["region"] ?? string.Empty;

                if (!UUID.TryParse(primUuidStr, out UUID primUuid) || primUuid == UUID.Zero)
                {
                    statusCode   = 400;
                    responseJson = JsonError("Invalid or missing 'uuid' parameter.");
                }
                else
                {
                    responseJson = FindPrimAndBuildJson(primUuid, regionFilter, out statusCode);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[PRIM TEXTURE] Request handler error: {0}", ex);
                statusCode   = 500;
                responseJson = JsonError("Internal server error: " + ex.Message);
            }

            byte[] buf = Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.StatusCode  = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buf.Length;

            try
            {
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { /* client disconnected */ }
        }

        // ----------------------------------------------------------------
        // Prim lookup and JSON building
        // ----------------------------------------------------------------

        private string FindPrimAndBuildJson(UUID primUuid, string regionFilter, out int statusCode)
        {
            List<Scene> scenes;
            lock (m_scenes)
                scenes = new List<Scene>(m_scenes);

            foreach (Scene scene in scenes)
            {
                // Optional region filter
                if (!string.IsNullOrEmpty(regionFilter) &&
                    !scene.RegionInfo.RegionName.Equals(regionFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                SceneObjectPart part = scene.GetSceneObjectPart(primUuid);
                if (part == null)
                    continue;

                // Found it
                statusCode = 200;
                return BuildJson(part, scene.RegionInfo.RegionName);
            }

            statusCode = 404;
            return JsonError($"Prim '{primUuid}' not found on this server" +
                (string.IsNullOrEmpty(regionFilter) ? "." : $" in region '{regionFilter}'."));
        }

        private string BuildJson(SceneObjectPart part, string regionName)
        {
            Primitive.TextureEntry te = part.Shape.Textures;
            if (te == null)
                return JsonError("Prim has no TextureEntry.");

            // We need the scene to fetch material assets - find it once
            Scene scene = null;
            lock (m_scenes)
                foreach (Scene s in m_scenes)
                    if (s.RegionInfo.RegionName == regionName) { scene = s; break; }

            var sb = new StringBuilder();
            sb.Append("{");
            sb.AppendFormat("\"prim_uuid\":\"{0}\",", part.UUID);
            sb.AppendFormat("\"prim_name\":{0},",     JsonString(part.Name));
            sb.AppendFormat("\"region\":{0},",        JsonString(regionName));

            // Parent SOG info
            sb.AppendFormat("\"parent_group_uuid\":\"{0}\",", part.ParentGroup?.UUID ?? UUID.Zero);
            sb.AppendFormat("\"parent_group_name\":{0},",
                JsonString(part.ParentGroup?.Name ?? string.Empty));

            // Mesh asset UUID (SculptTexture is overloaded as mesh asset UUID for mesh prims)
            bool isMesh = part.Shape.SculptType == 5; // 5 = Mesh in OpenSim
            sb.AppendFormat("\"is_mesh\":{0},",       isMesh ? "true" : "false");
            sb.AppendFormat("\"mesh_asset\":\"{0}\",", isMesh ? part.Shape.SculptTexture.ToString() : UUID.Zero.ToString());

            sb.Append("\"faces\":[");

            int faceCount = GetFaceCount(part);

            for (int face = 0; face < faceCount; face++)
            {
                if (face > 0) sb.Append(",");

                Primitive.TextureEntryFace faceData = te.GetFace((uint)face);
                UUID diffuse    = faceData?.TextureID  ?? UUID.Zero;
                UUID materialId = faceData?.MaterialID ?? UUID.Zero;

                // Resolve material asset to get actual normal + specular UUIDs
                UUID normalMap   = UUID.Zero;
                UUID specularMap = UUID.Zero;
                ResolveMaterial(scene, materialId, out normalMap, out specularMap);

                sb.Append("{");
                sb.AppendFormat("\"face\":{0},",           face);
                sb.AppendFormat("\"diffuse\":\"{0}\",",    diffuse);
                sb.AppendFormat("\"normal\":\"{0}\",",     normalMap);
                sb.AppendFormat("\"specular\":\"{0}\",",   specularMap);
                sb.AppendFormat("\"material_id\":\"{0}\"", materialId);

                if (faceData != null)
                {
                    sb.AppendFormat(",\"color\":\"<{0:F3},{1:F3},{2:F3},{3:F3}>\"",
                        faceData.RGBA.R, faceData.RGBA.G, faceData.RGBA.B, faceData.RGBA.A);
                    sb.AppendFormat(",\"fullbright\":{0}", faceData.Fullbright ? "true" : "false");
                    sb.AppendFormat(",\"glow\":{0:F4}",    faceData.Glow);
                    sb.AppendFormat(",\"shiny\":{0}",      (int)faceData.Shiny);
                }

                sb.Append("}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Resolves a Blinn-Phong material asset UUID to its normal and specular map UUIDs.
        /// The material asset is an LLSD XML blob stored in the asset service.
        ///
        /// LLSD structure (relevant fields only):
        ///   &lt;map&gt;
        ///     &lt;key&gt;NormMap&lt;/key&gt;  &lt;uuid&gt;...&lt;/uuid&gt;
        ///     &lt;key&gt;SpecMap&lt;/key&gt;  &lt;uuid&gt;...&lt;/uuid&gt;
        ///   &lt;/map&gt;
        /// </summary>
        private void ResolveMaterial(Scene scene, UUID materialId, out UUID normalMap, out UUID specularMap)
        {
            normalMap   = UUID.Zero;
            specularMap = UUID.Zero;

            if (scene == null || materialId == UUID.Zero)
                return;

            try
            {
                AssetBase asset = scene.AssetService.Get(materialId.ToString());
                if (asset == null || asset.Data == null || asset.Data.Length == 0)
                    return;

                string xml = Encoding.UTF8.GetString(asset.Data);

                // The LLSD XML looks like:
                //   <?xml ...?><llsd><map><key>NormMap</key><uuid>xxx</uuid>
                //                        <key>SpecMap</key><uuid>xxx</uuid>...</map></llsd>
                // We use simple string parsing to avoid pulling in a full XML dependency.
                // This is safe because UUIDs are fixed-format and keys are known.

                normalMap   = ExtractLlsdUuid(xml, "NormMap");
                specularMap = ExtractLlsdUuid(xml, "SpecMap");
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[PRIM TEXTURE] Failed to resolve material {0}: {1}",
                    materialId, ex.Message);
            }
        }

        /// <summary>
        /// Extracts the UUID value that follows a given LLSD key in an LLSD XML string.
        /// Looks for: &lt;key&gt;keyName&lt;/key&gt;&lt;uuid&gt;VALUE&lt;/uuid&gt;
        /// </summary>
        private static UUID ExtractLlsdUuid(string llsdXml, string keyName)
        {
            string keyTag   = $"<key>{keyName}</key>";
            int keyIndex    = llsdXml.IndexOf(keyTag, StringComparison.Ordinal);
            if (keyIndex < 0)
                return UUID.Zero;

            int uuidOpen  = llsdXml.IndexOf("<uuid>",  keyIndex + keyTag.Length, StringComparison.Ordinal);
            int uuidClose = llsdXml.IndexOf("</uuid>", keyIndex + keyTag.Length, StringComparison.Ordinal);

            if (uuidOpen < 0 || uuidClose < 0 || uuidClose <= uuidOpen)
                return UUID.Zero;

            int start     = uuidOpen + "<uuid>".Length;
            string uuidStr = llsdXml.Substring(start, uuidClose - start).Trim();

            return UUID.TryParse(uuidStr, out UUID result) ? result : UUID.Zero;
        }

        /// <summary>
        /// Returns the number of texture faces for a prim.
        /// Always returns 8 (the SL/OpenSim protocol maximum).
        /// Unset faces return the default texture from the TextureEntry,
        /// so callers should filter out the blank/default UUID if needed.
        /// This avoids dependency on shape property names that vary across OpenSim builds.
        /// </summary>
        private int GetFaceCount(SceneObjectPart part)
        {
            return 8;
        }

        // ----------------------------------------------------------------
        // JSON helpers  (no external dependency)
        // ----------------------------------------------------------------

        private static string JsonError(string message)
        {
            return $"{{\"error\":{JsonString(message)}}}";
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\")
                           .Replace("\"", "\\\"")
                           .Replace("\n", "\\n")
                           .Replace("\r", "\\r")
                           .Replace("\t", "\\t") + "\"";
        }
    }
}

