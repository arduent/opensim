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

[assembly: Addin("ParentalControlModule", "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("OpenSim Addin for ParentalControls")]
[assembly: AddinAuthor("Fiona Sweet fiona@pobox.holoneon.com")]

namespace OpenSim.Region.Modules.ParentalControl
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class ParentalControlModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        private string m_connectionString;
        private bool m_enabled = false;
        private int m_adminPort = 9100;
        private string m_listenerIP = "127.0.0.1";
        private bool m_parentBypass = true;

        private HttpListener m_httpListener;
        private ConcurrentDictionary<UUID, UUID> m_restrictedCache = new ConcurrentDictionary<UUID, UUID>();

        public string Name => "ParentalControlModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ParentalControls"];
            if (config == null || !config.GetBoolean("Enabled", false)) return;

            m_enabled = true;
            m_adminPort = config.GetInt("AdminPort", 9100);
            m_listenerIP = config.GetString("ListenerIP", "127.0.0.1");
            m_parentBypass = config.GetBoolean("AllowParentBypass", true);

            IConfig dbConfig = source.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                m_connectionString = dbConfig.GetString("ConnectionString", "");
                CheckTables(); 
            }
            m_log.Info("[PARENTAL] Module Initialized.");
        }

        public void PostInitialise() { if (m_enabled) StartHttpListener(); }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            scene.EventManager.OnIncomingInstantMessage += HandleIncomingIM;
            m_log.WarnFormat("[PARENTAL] AUDIT: Hooked Scene Pipeline for {0}", scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            scene.EventManager.OnNewClient -= OnNewClient;
        }

        public void RegionLoaded(Scene scene) { }
        public void Close() { if (m_httpListener != null) m_httpListener.Stop(); }



private void OnNewClient(IClientAPI client)
{
    UpdateCacheForUser(client.AgentId);
    // client.OnInstantMessage is removed here if you use the Scene Hook below
    client.OnSetStartLocationRequest += HandleSetHome; 
}

// This is the Scene-level handler that replaces HandleInstantMessage
private void HandleIncomingIM(GridInstantMessage im)
{
    UUID senderID = new UUID(im.fromAgentID);
    UUID recipientID = new UUID(im.toAgentID);

    // Audit everything as WARN so it always hits the log
    m_log.WarnFormat("[PARENTAL AUDIT] IM: {0} ({1}) -> {2}", im.fromAgentName, senderID, recipientID);

    // If we need to send an alert to the SENDER, we find them in the scene
    // This is how we avoid the "client does not exist" error
    foreach (Scene scene in SceneManager.Instance.Scenes)
    {
        if (scene.TryGetClient(senderID, out IClientAPI senderClient))
        {
            // Now you have 'senderClient' to send alerts if needed
            // senderClient.SendAgentAlertMessage("Blocked!", false);
            break;
        }
    }
}

// Helper to find the right scene for a specific agent
private Scene GetSceneByUUID(UUID agentID)
{
    // Since this is a Shared Module, we have to find which region the user is currently in
    // This is a standard OpenSim way to resolve a scene
    return SceneManager.Instance.Scenes.Find(s => s.Entities.ContainsKey(agentID));
}

/*
        private void HandleInstantMessage(IClientAPI client, GridInstantMessage im)
        {
            UUID senderID = new UUID(im.fromAgentID);
            UUID recipientID = new UUID(im.toAgentID);

            m_log.WarnFormat("[PARENTAL AUDIT] Pipeline Intercept: {0} -> {1}", im.fromAgentID, im.toAgentID);

            // 1. BLOCK FRIEND REQUESTS (Dialog 38)
            // This replaces the OnAddFriend hook that was failing to compile.
            if (im.dialog == (byte)38) 
            {
                if (IsRestricted(senderID, out _) || IsRestricted(recipientID, out _))
                {
                    m_log.WarnFormat("[PARENTAL] Blocked Incoming Instant Message Attempt: {0} ({1}) contacted {1}", senderID, im.fromAgentName, recipientID);
                    client.SendAgentAlertMessage("Parental Controls: Friend requests must be handled via the web portal.", false);
                    return; // Drop the friendship offer
                }
            }

            // 2. BLOCK INCOMING TO CHILD
            if (IsRestricted(recipientID, out UUID pID) && (!m_parentBypass || senderID != pID))
            {
                if (!IsFriend(recipientID, senderID)) return;
            }

            // 3. BLOCK OUTGOING FROM CHILD
            if (IsRestricted(senderID, out pID) && (!m_parentBypass || recipientID != pID))
            {
                if (!IsFriend(senderID, recipientID))
                {
                    m_log.WarnFormat("[PARENTAL] Blocked Outgoing Instant Message Attempt: {0} contacted {1}", senderID, recipientID);
                    client.SendAgentAlertMessage("Parental Controls: Approved friends only.", false);
                    return;
                }
            }
        }

*/
        private void HandleSetHome(IClientAPI client, ulong regionHandle, Vector3 pos, Vector3 lookAt, uint locationID)
        {
            if (IsRestricted(client.AgentId, out _))
            {
                client.SendAgentAlertMessage("Parental Controls: Setting home is disabled.", false);
            }
        }

        private void CheckTables()
        {
            using var conn = new MySqlConnection(m_connectionString);
            try {
                conn.Open();
                using var cmd = new MySqlCommand("CREATE TABLE IF NOT EXISTS `parental_links` (`ChildID` CHAR(36) NOT NULL, `ParentID` CHAR(36) NOT NULL, `IsRestricted` TINYINT(1) DEFAULT 1, PRIMARY KEY (`ChildID`)) ENGINE=InnoDB;", conn);
                cmd.ExecuteNonQuery();
            } catch (Exception e) { m_log.Error("[PARENTAL] DB Error: " + e.Message); }
        }

        private void UpdateCacheForUser(UUID agentID)
        {
            using var conn = new MySqlConnection(m_connectionString);
            try {
                conn.Open();
                using var cmd = new MySqlCommand("SELECT ParentID FROM parental_links WHERE ChildID = @cid AND IsRestricted = 1", conn);
                cmd.Parameters.AddWithValue("@cid", agentID.ToString());
                var res = cmd.ExecuteScalar();
                m_restrictedCache[agentID] = (res != null) ? new UUID(res.ToString()) : UUID.Zero;
            } catch { }
        }

        private bool IsRestricted(UUID agentID, out UUID parentID) => m_restrictedCache.TryGetValue(agentID, out parentID) && parentID != UUID.Zero;

        private bool IsFriend(UUID principalID, UUID friendID)
        {
            using var conn = new MySqlConnection(m_connectionString);
            try {
                conn.Open();
                using var cmd = new MySqlCommand("SELECT COUNT(*) FROM Friends WHERE PrincipalID = @pid AND Friend = @fid", conn);
                cmd.Parameters.AddWithValue("@pid", principalID.ToString());
                cmd.Parameters.AddWithValue("@fid", friendID.ToString());
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            } catch { return false; }
        }

        private void StartHttpListener()
        {
            try {
                m_httpListener = new HttpListener();
                m_httpListener.Prefixes.Add($"http://{m_listenerIP}:{m_adminPort}/flush/");
                m_httpListener.Start();
                m_httpListener.BeginGetContext(OnHttpRequest, m_httpListener);
            } catch (Exception e) { m_log.Error("[PARENTAL] Listener Error: " + e.Message); }
        }

        private void OnHttpRequest(IAsyncResult result)
        {
            if (!m_httpListener.IsListening) return;
            try {
                var context = m_httpListener.EndGetContext(result);
                string target = context.Request.QueryString["uuid"];

		m_log.WarnFormat("[PARENTAL] received Flush request: uuid {1}", target);
                if (!string.IsNullOrEmpty(target)) UpdateCacheForUser(new UUID(target));
                context.Response.Close();
                m_httpListener.BeginGetContext(OnHttpRequest, m_httpListener);
            } catch { }
        }
    }
}

