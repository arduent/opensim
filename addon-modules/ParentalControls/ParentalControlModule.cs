using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.IO;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using Nini.Config;
using MySql.Data.MySqlClient;

[assembly: Addin("ParentalControlModule", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace OpenSim.Region.Modules.ParentalControl
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class ParentalControlModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        // Configuration variables
        private string m_connectionString;
        private bool m_enabled = false;
        private int m_adminPort = 9100;
        private string m_listenerIP = "10.99.0.1";
        private bool m_blockTeleports = false;
        private bool m_parentBypass = true;

        private HttpListener m_httpListener;
        
        // Cache: Key = ChildUUID, Value = ParentUUID (UUID.Zero if not restricted)
        private ConcurrentDictionary<UUID, UUID> m_restrictedCache = new ConcurrentDictionary<UUID, UUID>();

        public string Name { get { return "ParentalControlModule"; } }
        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ParentalControls"];
            if (config == null || !config.GetBoolean("Enabled", false))
            {
                m_enabled = false;
                return;
            }

            m_enabled = true;
            m_listenerIP = config.GetString("ListenerIP", "10.99.0.1");
            m_adminPort = config.GetInt("AdminPort", 9100);
            m_blockTeleports = config.GetBoolean("BlockAllTeleports", false);
            m_parentBypass = config.GetBoolean("AllowParentBypass", true);

            // Load Database Settings from OpenSim system config
            IConfig dbConfig = source.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                m_connectionString = dbConfig.GetString("ConnectionString", "");
                CheckTables(); 
            }
            
            m_log.Info("[PARENTAL] Module initialized and enabled.");
        }

        public void PostInitialise()
        {
            if (m_enabled) StartHttpListener();
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            scene.EventManager.OnNewClient += OnNewClient;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            scene.EventManager.OnNewClient -= OnNewClient;
        }

        public void RegionLoaded(Scene scene) { }

        public void Close() 
        { 
            if (m_httpListener != null && m_httpListener.IsListening) 
                m_httpListener.Stop(); 
        }

        private void OnNewClient(IClientAPI client)
        {
            // Populate cache for the user immediately on login/entry
            UpdateCacheForUser(client.AgentId);

            client.OnInstantMessage += HandleInstantMessage;
            client.OnAddFriend += HandleAddFriend;
            client.OnSetStartLocationRequest += HandleSetHome;
            client.OnRequestTeleport += HandleTeleport;
            
            // Clean up cache on logout to prevent memory bloat
            client.OnLogout += (c) => { 
                UUID dummy; 
                m_restrictedCache.TryRemove(c.AgentId, out dummy); 
            };
        }

        private void HandleInstantMessage(IClientAPI client, GridInstantMessage im)
        {
            UUID senderID = new UUID(im.fromAgentID);
            UUID recipientID = new UUID(im.toAgentID);
            UUID parentID;

            // Check if Recipient is Restricted
            if (IsRestricted(recipientID, out parentID))
            {
                if (m_parentBypass && senderID == parentID) return; // Parent Bypass
                if (!IsFriend(recipientID, senderID)) return; // Drop packet
            }

            // Check if Sender is Restricted
            if (IsRestricted(senderID, out parentID))
            {
                if (m_parentBypass && recipientID == parentID) return; // Parent Bypass
                if (!IsFriend(senderID, recipientID))
                {
                    client.SendAgentAlertMessage("Parental Controls: You may only contact approved friends.", false);
                    return; // Drop packet
                }
            }
        }

        private void HandleAddFriend(IClientAPI client, UUID victimID)
        {
            UUID parentID;
            if (IsRestricted(client.AgentId, out parentID))
            {
                client.SendAgentAlertMessage("You cannot send friend requests. Contact your parent.", false);
            }
        }

        private void HandleSetHome(IClientAPI client, ulong regionHandle, Vector3 pos, Vector3 lookAt, uint locationID)
        {
            UUID parentID;
            if (IsRestricted(client.AgentId, out parentID))
            {
                client.SendAgentAlertMessage("Parental Controls: Setting home location is disabled.", false);
            }
        }

        private void HandleTeleport(IClientAPI client, uint regionHandle, Vector3 pos, Vector3 lookAt, uint flags, IClientAPI remoteClient)
        {
            if (!m_blockTeleports) return;

            UUID parentID;
            if (IsRestricted(client.AgentId, out parentID))
            {
                client.SendAgentAlertMessage("Parental Controls: Teleporting is restricted for this account.", true);
            }
        }

        // --- Database & Cache Logic ---

        private void CheckTables()
        {
            using (MySqlConnection conn = new MySqlConnection(m_connectionString))
            {
                try {
                    conn.Open();
                    string sql = @"CREATE TABLE IF NOT EXISTS `parental_links` (
                        `ChildID` CHAR(36) NOT NULL,
                        `ParentID` CHAR(36) NOT NULL,
                        `IsRestricted` TINYINT(1) DEFAULT 1,
                        PRIMARY KEY (`ChildID`)
                    ) ENGINE=InnoDB;";
                    using (MySqlCommand cmd = new MySqlCommand(sql, conn)) {
                        cmd.ExecuteNonQuery();
                    }
                } catch (Exception e) { m_log.Error("[PARENTAL] DB Schema Error: " + e.Message); }
            }
        }

        private void UpdateCacheForUser(UUID agentID)
        {
            using (MySqlConnection conn = new MySqlConnection(m_connectionString))
            {
                try {
                    conn.Open();
                    string sql = "SELECT ParentID FROM parental_links WHERE ChildID = @cid AND IsRestricted = 1";
                    using (MySqlCommand cmd = new MySqlCommand(sql, conn)) {
                        cmd.Parameters.AddWithValue("@cid", agentID.ToString());
                        object res = cmd.ExecuteScalar();
                        m_restrictedCache[agentID] = (res != null) ? new UUID(res.ToString()) : UUID.Zero;
                    }
                } catch (Exception e) { m_log.Error("[PARENTAL] Cache Update Error: " + e.Message); }
            }
        }

        private bool IsRestricted(UUID agentID, out UUID parentID)
        {
            return m_restrictedCache.TryGetValue(agentID, out parentID) && parentID != UUID.Zero;
        }

        private bool IsFriend(UUID principalID, UUID friendID)
        {
            using (MySqlConnection conn = new MySqlConnection(m_connectionString))
            {
                try {
                    conn.Open();
                    string sql = "SELECT COUNT(*) FROM Friends WHERE PrincipalID = @pid AND Friend = @fid";
                    using (MySqlCommand cmd = new MySqlCommand(sql, conn)) {
                        cmd.Parameters.AddWithValue("@pid", principalID.ToString());
                        cmd.Parameters.AddWithValue("@fid", friendID.ToString());
                        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                    }
                } catch { return false; }
            }
        }

        // --- HTTP Admin Listener ---

        private void StartHttpListener()
        {
            try {
                m_httpListener = new HttpListener();
                m_httpListener.Prefixes.Add(string.Format("http://{0}:{1}/flush/", m_listenerIP, m_adminPort));
                m_httpListener.Start();
                m_httpListener.BeginGetContext(OnHttpRequest, m_httpListener);
                m_log.InfoFormat("[PARENTAL] Admin listener active on port {0}", m_adminPort);
            } catch (Exception e) { m_log.Error("[PARENTAL] HttpListener Error: " + e.Message); }
        }

        private void OnHttpRequest(IAsyncResult result)
        {
            if (!m_httpListener.IsListening) return;
            try {
                HttpListenerContext context = m_httpListener.EndGetContext(result);
                string targetUser = context.Request.QueryString["uuid"];
                if (!string.IsNullOrEmpty(targetUser)) {
                    UpdateCacheForUser(new UUID(targetUser));
                    m_log.InfoFormat("[PARENTAL] External cache flush for {0}", targetUser);
                }
                context.Response.StatusCode = 200;
                context.Response.Close();
                m_httpListener.BeginGetContext(OnHttpRequest, m_httpListener);
            } catch { }
        }
    }
}

