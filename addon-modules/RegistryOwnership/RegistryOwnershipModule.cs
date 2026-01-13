// dotnet new classlib -n Holoneon.RegistryOwnership
//OpenSim.Framework.dll
//OpenSim.Region.Framework.dll
//OpenMetaverse.dll

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenMetaverse;

namespace Holoneon.Modules
{
    public class RegistryOwnershipModule : IRegionModule
    {
        private Scene m_scene;
        private Timer m_timer;

        private string m_registryBase;
        private int m_pollInterval;

        private readonly Dictionary<string, AssetRule> m_assets = new();

        public string Name => "RegistryOwnershipModule";
        public bool IsSharedModule => false;

        public void Initialise(Scene scene, IConfigSource source)
        {
            var cfg = source.Configs["RegistryOwnership"];
            if (cfg == null || !cfg.GetBoolean("Enabled", false))
                return;

            m_scene = scene;
            m_registryBase = cfg.GetString("RegistryBaseUrl");
            m_pollInterval = cfg.GetInt("PollIntervalSeconds", 300);

            foreach (string key in cfg.GetKeys())
            {
                if (!key.StartsWith("Asset_")) continue;

                string[] parts = cfg.GetString(key).Split('|');
                if (parts.Length != 3) continue;

                m_assets[parts[0].Trim()] = new AssetRule
                {
                    CertId = parts[0].Trim(),
                    GroupName = parts[1].Trim(),
                    RoleName = parts[2].Trim(),
                    LastOwner = null
                };
            }
        }

        public void PostInitialise()
        {
            if (m_scene == null || m_assets.Count == 0)
                return;

            m_timer = new Timer(PollRegistry, null, 10_000, m_pollInterval * 1000);
        }

        public void Close()
        {
            m_timer?.Dispose();
        }

        public void AddRegion(Scene scene) { }
        public void RemoveRegion(Scene scene) { }

        public void RegionLoaded(Scene scene) { }

        private async void PollRegistry(object state)
        {
            using var http = new HttpClient();

            foreach (var rule in m_assets.Values)
            {
                try
                {
                    string url = $"{m_registryBase}/certs/{rule.CertId}";
                    var json = await http.GetStringAsync(url);
                    var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.GetProperty("ok").GetBoolean())
                        continue;

                    string owner = doc.RootElement.GetProperty("current_owner").GetString();

                    if (owner == rule.LastOwner)
                        continue;

                    rule.LastOwner = owner;
                    ApplyOwnership(rule, owner);
                }
                catch (Exception e)
                {
                    m_scene?.SimChat($"[RegistryOwnership] Error: {e.Message}", ChatTypeEnum.DebugChannel, 0, Vector3.Zero, UUID.Zero, "");
                }
            }
        }

        private void ApplyOwnership(AssetRule rule, string ownerHg)
        {
            var groups = m_scene.RequestModuleInterface<IGroupsModule>();
            if (groups == null) return;

            UUID groupId = groups.GetGroupByName(m_scene.RegionInfo.ScopeID, rule.GroupName);
            if (groupId == UUID.Zero) return;

            UUID roleId = groups.GetRoleByName(groupId, rule.RoleName);
            if (roleId == UUID.Zero) return;

            // Remove role from all members
            foreach (var member in groups.GetGroupMembers(groupId))
            {
                groups.RemoveAgentFromGroupRole(groupId, roleId, member.AgentID);
            }

            UUID agentId = ResolveHgOwner(ownerHg);
            if (agentId == UUID.Zero)
            {
                // Owner not present yet – will apply on login
                return;
            }

            groups.AddAgentToGroupRole(groupId, roleId, agentId);
        }

        private UUID ResolveHgOwner(string ownerHg)
        {
            // Expect hg://grid/First Last
            int p = ownerHg.LastIndexOf('/');
            if (p == -1) return UUID.Zero;

            string name = ownerHg.Substring(p + 1);
            string[] parts = name.Split(' ');
            if (parts.Length < 2) return UUID.Zero;

            string first = parts[0];
            string last = parts[1];

            var account = m_scene.UserAccountService.GetUserAccount(
                m_scene.RegionInfo.ScopeID,
                first,
                last
            );

            return account?.PrincipalID ?? UUID.Zero;
        }

        private class AssetRule
        {
            public string CertId;
            public string GroupName;
            public string RoleName;
            public string LastOwner;
        }
    }
}

