using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Nini.Config;
using Mono.Addins;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenMetaverse;

[assembly: Addin("RegistryOwnershipModule", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace Holoneon.Modules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegistryOwnershipModule")]
    public class RegistryOwnershipModule : ISharedRegionModule
    {

        private const int MINT_CHANNEL = -987654;
        private static readonly UUID NFT_MINT_REGION_UUID =
            new("3b314e77-ff2e-42d6-b884-c23c89de0a6b");

        private static readonly ILog m_log = LogManager.GetLogger("RegistryOwnership");
        private readonly List<Scene> m_scenes = new();
        private Timer m_timer;
        
        private string m_registryBase;
        private string m_keyId;
        private byte[] m_secretKeyBytes;
        private int m_pollIntervalSeconds = 300;
        private bool m_enforceOnlyWhenVerified = true;

        private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

        // ---------------- ISharedRegionModule ----------------

        public string Name => "RegistryOwnershipModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var cfg = source.Configs["RegistryOwnership"];
            if (cfg == null || !cfg.GetBoolean("Enabled", false))
                return;

            m_registryBase = cfg.GetString("RegistryBaseUrl", "").TrimEnd('/');
            m_keyId = cfg.GetString("RegionKeyId", "");
            string secretB64 = cfg.GetString("RegionSecretKeyB64", "");

            try
            {
                m_secretKeyBytes = Convert.FromBase64String(secretB64);
            }
            catch (FormatException)
            {
                m_log.Error("[RegistryOwnership]: RegionSecretKeyB64 is not valid Base64");
                return;
            }

            if (m_secretKeyBytes.Length < 16)
            {
                m_log.Error("[RegistryOwnership]: RegionSecretKeyB64 too short");
                return;
            }

            m_pollIntervalSeconds = cfg.GetInt("PollIntervalSeconds", 300);
            m_enforceOnlyWhenVerified = cfg.GetBoolean("EnforceOnlyWhenVerified", true);

            if (string.IsNullOrEmpty(m_registryBase))
            {
                m_log.Error("[RegistryOwnership]: RegistryBaseUrl not configured. Module disabled.");
                return;
            }

            m_log.Info("[RegistryOwnership]: Module Initialising with HMAC Security");

            m_timer = new Timer(_ => _ = PollRegistryAsync(), null, 
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(m_pollIntervalSeconds));
        }

        public void AddRegion(Scene scene)
        {
            lock (m_scenes)
            {
                if (!m_scenes.Contains(scene))
                    m_scenes.Add(scene);
            }

            scene.EventManager.OnChatFromClient += OnChatFromClient;

            m_log.InfoFormat("[RegistryOwnership]: Added to region {0}", scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnChatFromClient -= OnChatFromClient;
            lock (m_scenes) { m_scenes.Remove(scene); }
        }

        public void RegionLoaded(Scene scene) { }
        public void PostInitialise() { }
        public void Close() { m_timer?.Dispose(); }

        // ---------------- Polling and Security ----------------

        private async Task PollRegistryAsync()
        {
            Scene[] currentScenes;
            lock (m_scenes) { currentScenes = m_scenes.ToArray(); }

            foreach (var scene in currentScenes)
            {
                try { await PollRegistryForSceneAsync(scene); }
                catch (Exception ex)
                {
                    m_log.Error($"[RegistryOwnership]: Poll failed for {scene.RegionInfo.RegionName}", ex);
                }
            }
        }

        private async Task PollRegistryForSceneAsync(Scene scene)
        {
            string region = scene.RegionInfo.RegionName;
            // The path used for signature must match exactly what PHP receives
            string path = $"/certs/enforced?region={Uri.EscapeDataString(region)}";
            string url = $"{m_registryBase}{path}";
            string canonicalPath = "/certs/enforced";

            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string nonce = Guid.NewGuid().ToString("N");

            // Generate HMAC signature matching lib_registry.php
            string signature = GenerateSignature("GET", canonicalPath, ts, nonce, "", m_secretKeyBytes);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Holoneon-OwnerHG", Uri.EscapeDataString(scene.RegionInfo.EstateSettings.EstateOwner.ToString()));
            request.Headers.Add("X-Holoneon-Region", region);
            request.Headers.Add("X-Holoneon-TS", ts);
            request.Headers.Add("X-Holoneon-Nonce", nonce);
            request.Headers.Add("X-Holoneon-KeyID", m_keyId);
            request.Headers.Add("X-Holoneon-Signature", signature);

            using var response = await s_http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                m_log.WarnFormat("[RegistryOwnership]: API returned {0} for {1}", response.StatusCode, region);
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) return;

            foreach (var cert in doc.RootElement.GetProperty("certs").EnumerateArray())
            {
                await HandleEnforcedCertAsync(scene, cert);
            }
        }

        private string GenerateSignature(string method, string path, string ts, string nonce, string body, byte[] m_secretKeyBytes)
        {
            // Canonical format: METHOD\nPATH\nTIMESTAMP\nNONCE\nBODYHASH
            string bodyHash = "";
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(body));
                bodyHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }

            string canonical = $"{method}\n{path}\n{ts}\n{nonce}\n{bodyHash}";
            
            using (var hmac = new HMACSHA256(m_secretKeyBytes))
            {
                byte[] sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return Convert.ToBase64String(sigBytes);
            }
        }

        // ---------------- Enforcement ----------------

        private async Task HandleEnforcedCertAsync(Scene scene, JsonElement cert)
        {
            string ownerHg = cert.GetProperty("owner_hg").GetString();
            if (string.IsNullOrEmpty(ownerHg)) return;

            if (m_enforceOnlyWhenVerified && !await VerifyAllSnapshotsAsync(cert)) return;

            if (!cert.TryGetProperty("enforcement", out var enforcement)) return;

            string type = enforcement.GetProperty("type").GetString();
            if (type == "group_role") EnforceGroupRole(scene, enforcement, ownerHg);
            else if (type == "parcel_role") EnforceParcelRole(scene, enforcement, ownerHg);
        }

        private void EnforceGroupRole(Scene scene, JsonElement enforcement, string ownerHg)
        {
            var groupsModule = scene.RequestModuleInterface<IGroupsModule>();
            if (groupsModule == null) return;

            string groupName = enforcement.GetProperty("group").GetString();
            string roleName = enforcement.GetProperty("role").GetString();

            GroupRecord group = groupsModule.GetGroupRecord(groupName);
            if (group == null) return;

            var roles = groupsModule.GroupRoleDataRequest(UUID.Zero, group.GroupID);
            if (roles == null) return;

            // Non-nullable extraction for struct
            var role = roles.FirstOrDefault(r => r.Name == roleName);
            if (role.RoleID == UUID.Zero) return;

            UUID roleId = role.RoleID;
            UUID ownerId = ResolveHgOwner(scene, ownerHg);
            if (ownerId == UUID.Zero) return;

            // Groups V2 use GroupRoleMembersRequest and GroupRoleChanges
            var members = groupsModule.GroupRoleMembersRequest(null, group.GroupID);
            if (members != null)
            {
                foreach (var m in members)
                {
                    if (m.RoleID == roleId)
                        groupsModule.GroupRoleChanges(null, group.GroupID, roleId, m.MemberID, 0); // 0 = Remove
                }
            }
            groupsModule.GroupRoleChanges(null, group.GroupID, roleId, ownerId, 1); // 1 = Add
        }

        private void EnforceParcelRole(Scene scene, JsonElement enforcement, string ownerHg)
        {
            var groupsModule = scene.RequestModuleInterface<IGroupsModule>();
            if (groupsModule == null) return;

            string parcelName = enforcement.GetProperty("parcel_name").GetString();
            string groupName = enforcement.GetProperty("group").GetString();
            string roleName = enforcement.GetProperty("role").GetString();

            var parcel = scene.LandChannel.AllParcels().FirstOrDefault(p => p.LandData.Name == parcelName);
            if (parcel == null) return;

            GroupRecord group = groupsModule.GetGroupRecord(groupName);
            if (group == null) return;

            var roles = groupsModule.GroupRoleDataRequest(UUID.Zero, group.GroupID);
            if (roles == null) return;

            var role = roles.FirstOrDefault(r => r.Name == roleName);
            if (role.RoleID == UUID.Zero) return;

            UUID roleId = role.RoleID;
            UUID ownerId = ResolveHgOwner(scene, ownerHg);
            if (ownerId == UUID.Zero) return;

            var members = groupsModule.GroupRoleMembersRequest(null, group.GroupID);
            if (members != null)
            {
                foreach (var m in members)
                {
                    if (m.RoleID == roleId)
                        groupsModule.GroupRoleChanges(null, group.GroupID, roleId, m.MemberID, 0);
                }
            }
            groupsModule.GroupRoleChanges(null, group.GroupID, roleId, ownerId, 1);
        }

        // ---------------- Utilities ----------------

        private async Task<bool> VerifyAllSnapshotsAsync(JsonElement cert)
        {
            if (!cert.TryGetProperty("snapshots", out var snaps)) return true;
            foreach (var snap in snaps.EnumerateArray())
            {
                if (!await VerifySnapshotAsync(snap)) return false;
            }
            return true;
        }

        private async Task<bool> VerifySnapshotAsync(JsonElement snap)
        {
            try
            {
                string url = snap.GetProperty("url").GetString();
                string expected = snap.GetProperty("sha256").GetString();
                byte[] data = await s_http.GetByteArrayAsync(url);
                using var sha = SHA256.Create();
                string hash = Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
                return hash == expected;
            }
            catch { return false; }
        }

        private UUID ResolveHgOwner(Scene scene, string ownerHg)
        {
            string[] parts = ownerHg.Trim().Split(' ');
            if (parts.Length < 2) return UUID.Zero;

            // Resolve via UserAccountService
            var acct = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, parts[0], parts[1]);
            return acct?.PrincipalID ?? UUID.Zero;
        }

	private void OnChatFromClient(
	    object sender,
	    OSChatMessage msg
	)
	{
	    // Only listen on the private mint channel
	    if (msg.Channel != MINT_CHANNEL)
	        return;
	
	    Scene scene = msg.Scene as Scene;
            if (scene == null)
                return;

	
	    // Enforce mint region
	    if (scene.RegionInfo.RegionID != NFT_MINT_REGION_UUID)
	    {
	        m_log.Warn("[Mint]: Mint request from wrong region");
	        return;
	    }
	
	    // Expected format: MINT_REQUEST|<objectUUID>
	    if (!msg.Message.StartsWith("MINT_REQUEST|"))
	        return;
	
	    string[] parts = msg.Message.Split('|');
	    if (parts.Length != 2)
	        return;
	
	    if (!UUID.TryParse(parts[1], out UUID objectId))
	        return;
	
	    ScenePresence sp = scene.GetScenePresence(msg.Sender.AgentId);
	    if (sp == null || sp.IsDeleted)
	        return;
	
	    // Avatar is physically present — this is the authority check
	    UUID avatarId = sp.UUID;
	
	    // Now we can safely call the registry
	    _ = CreateMintChallengeAsync(
	        scene,
	        avatarId,
	        objectId
	    );
	}

	private async Task CreateMintChallengeAsync(
	    Scene scene,
	    UUID avatarId,
	    UUID objectId
	)
	{
	    try
	    {
	        var payload = new
	        {
	            avatar_uuid = avatarId.ToString(),
	            object_uuid = objectId.ToString(),
	            region_uuid = scene.RegionInfo.RegionID.ToString(),
	            owner_hg = $"{scene.RegionInfo.EstateSettings.EstateOwner}"
	        };
	
	        string body = JsonSerializer.Serialize(payload);
	
	        string path = "/mint/challenge";
	        string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
	        string nonce = Guid.NewGuid().ToString("N");
	
	        string signature = GenerateSignature(
	            "POST", path, ts, nonce, body, m_secretKeyBytes
	        );
	
	        var req = new HttpRequestMessage(
	            HttpMethod.Post,
	            $"{m_registryBase}{path}"
	        );
	
	        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
	        req.Headers.Add("X-Holoneon-KeyID", m_keyId);
	        req.Headers.Add("X-Holoneon-Region", scene.RegionInfo.RegionName);
	        req.Headers.Add("X-Holoneon-TS", ts);
	        req.Headers.Add("X-Holoneon-Nonce", nonce);
	        req.Headers.Add("X-Holoneon-Signature", signature);
	
	        var res = await s_http.SendAsync(req);
	        if (!res.IsSuccessStatusCode)
	        {
	            m_log.Warn($"[Mint]: Registry rejected mint ({res.StatusCode})");
	            return;
	        }
	
	        string json = await res.Content.ReadAsStringAsync();
	        m_log.Info($"[Mint]: Challenge created {json}");
	    }
	    catch (Exception ex)
	    {
	        m_log.Error("[Mint]: Failed to create challenge", ex);
	    }
	}
	
    }
}

