/*
 * ParentalControlsModule
 * for OpenSimulator 0.9.3.1
 * Copyright 2026 by Fiona Sweet <fiona@pobox.holoneon.com>
 * VERSION $0.1.20260128-01$
 * 
 * Redistribution and use in source and binary forms, with or without 
 * modification, are permitted provided that the following conditions 
 * are met:
 * 
 * Redistributions of source code must retain the above copyright 
 * notice, this list of conditions and the following disclaimer.
 * 
 * Redistributions in binary form must reproduce the above copyright 
 * notice, this list of conditions and the following disclaimer in the 
 * documentation and/or other materials provided with the distribution.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS 
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT 
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS 
 * FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE 
 * COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, 
 * INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; 
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER 
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT 
 * LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN 
 * ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.IO;

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Client;


using log4net;
using Nini.Config;
using MySql.Data.MySqlClient;
using Mono.Addins;

[assembly: Addin("ParentalControlModule", "0.1")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDescription("OpenSim Addin for ParentalControls")]
[assembly: AddinAuthor("Fiona Sweet fiona@pobox.holoneon.com")]

namespace OpenSim.Region.Modules.ParentalControl
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class ParentalControlModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_connectionString;
        private bool m_enabled = false;
        private int m_adminPort = 9100;
        private string m_listenerIP = "127.0.0.1";
        private bool m_parentBypass = true;

        private HttpListener m_httpListener;

        // ChildID -> ParentID (UUID.Zero if not restricted)
        private readonly ConcurrentDictionary<UUID, UUID> m_restrictedCache =
            new ConcurrentDictionary<UUID, UUID>();

        public string Name { get { return "ParentalControlModule"; } }
        public Type ReplaceableInterface { get { return null; } }

        public void Initialise(IConfigSource source)
        {
            m_log.Info("[PARENTAL] Initialise...");
            IConfig config = source.Configs["ParentalControls"];
            if (config == null || !config.GetBoolean("Enabled", false))
            {
                m_log.Info("[PARENTAL] Failed to load config");
                return;
            }

            m_enabled = true;
            m_adminPort = config.GetInt("AdminPort", 9100);
            m_listenerIP = config.GetString("ListenerIP", "127.0.0.1");
            m_parentBypass = config.GetBoolean("AllowParentBypass", true);

            IConfig dbConfig = source.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                m_connectionString = dbConfig.GetString("ConnectionString", "");
		m_log.InfoFormat("[PARENTAL] DB Connnection String {0}",m_connectionString);
                CheckTables();
            } else {
		m_log.Info("[PARENTAL] No Database Config Found!");
            }
            m_log.Info("[PARENTAL] Module Initialized.");
        }

        public void PostInitialise()
        {
            if (m_enabled)
                StartHttpListener();
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnNewClient += OnNewClient;

            m_log.WarnFormat(
                "[PARENTAL] AUDIT: Client hook enabled for region {0}",
                scene.RegionInfo.RegionName
            );
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnNewClient -= OnNewClient;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
            if (m_httpListener != null)
                m_httpListener.Stop();
        }

        private void OnNewClient(IClientAPI client)
        {
            UpdateCacheForUser(client.AgentId);

            client.OnInstantMessage += HandleInstantMessage;
            client.OnTeleportLureRequest += HandleTeleportLure;
            client.OnTeleportLocationRequest += HandleTeleportRequest;
            client.OnSetStartLocationRequest += HandleSetHome;

            m_log.WarnFormat(
                "[PARENTAL] Client hooked: {0} ({1})",
                client.Name,
                client.AgentId
            );
        }

        // Helper to find the right scene for a specific agent
        private Scene GetSceneByUUID(UUID agentID)
        {
            // Since this is a Shared Module, we have to find which 
            // region the user is currently in
            return SceneManager.Instance.Scenes.Find(s => s.Entities.ContainsKey(agentID));
        }

private void HandleInstantMessage(IClientAPI client, GridInstantMessage im)
{
    UUID senderID = new UUID(im.fromAgentID);
    UUID recipientID = new UUID(im.toAgentID);
    InstantMessageDialog dialogType = (InstantMessageDialog)im.dialog;

    // 1. Identify if the Child is involved
    bool senderIsChild = IsRestricted(senderID, out _);
    bool recipientIsChild = IsRestricted(recipientID, out _);

    // If no restricted child is involved, allow everything
    if (!senderIsChild && !recipientIsChild)
        return;

    // 2. Parent Bypass: If the other party is the Parent, always allow
    if (senderIsChild && IsRestricted(senderID, out UUID p1) && recipientID == p1) return;
    if (recipientIsChild && IsRestricted(recipientID, out UUID p2) && senderID == p2) return;

    // 3. Friendship Bypass: If they are already approved friends, allow
    if (IsFriend(senderID, recipientID) || IsFriend(recipientID, senderID))
        return;

    // 4. Block specific types for non-friends
    switch (dialogType)
    {
        case InstantMessageDialog.MessageFromAgent: // 0
        case InstantMessageDialog.InventoryOffered: // 4
        case InstantMessageDialog.GroupInvitation:  // 19
        case InstantMessageDialog.RequestTeleport:   // 22
        case InstantMessageDialog.FriendshipOffered: // 38
            
            m_log.WarnFormat("[PARENTAL] BLOCKED {0}: {1} -> {2}", 
                dialogType, senderID, recipientID);

            // Audit the block
            string currentRegion = client.Scene.RegionInfo.RegionName;
            LogAudit(dialogType.ToString(), senderID, recipientID, "RestrictedInteraction", currentRegion);

            // Kill the message
            im.dialog = (byte)255; 
            im.message = string.Empty;

            // Notify the Child specifically if they were the sender
            if (senderIsChild)
            {
                client.SendAgentAlertMessage(
                    $"Parental Controls: {dialogType} is restricted to approved friends.", 
                    false);
            }
            break;

        default:
            // Allow typing notifications (32) or friendship responses (39, 40)
            break;
    }
}

private void HandleTeleportRequest(IClientAPI client, ulong regionHandle, Vector3 pos, Vector3 lookAt, uint flags)
{
    if (!IsRestricted(client.AgentId, out UUID parentID))
        return;

    // Optional: Allow TP if the Parent is already in the destination region
    // Or simply block all TPs that aren't to 'Home'
    
    m_log.WarnFormat("[PARENTAL] BLOCKED TP Request for child {0}", client.Name);

    // Notify the child
    client.SendAgentAlertMessage("Parental Controls: Teleporting is restricted. Please have your parent move you.", false);

    // This is the key: by not calling any further logic, the TP request dies here.
}

private void HandleTeleportLure(
    UUID lureID,
    uint teleportFlags,
    IClientAPI client
)
{
    UUID senderID   = lureID;           // who sent the lure
    UUID recipientID = client.AgentId;  // who received it

    bool senderIsChild    = IsRestricted(senderID, out _);
    bool recipientIsChild = IsRestricted(recipientID, out _);

    // If neither side is restricted, allow
    if (!senderIsChild && !recipientIsChild)
        return;

    bool areFriends =
        IsFriend(senderID, recipientID) ||
        IsFriend(recipientID, senderID);

    if (areFriends)
        return;

    m_log.WarnFormat(
        "[PARENTAL] BLOCKED HG LURE {0} -> {1}",
        senderID,
        recipientID
    );

    // Audit log (metadata only)
    string currentRegion = client.Scene.RegionInfo.RegionName;
    LogAudit("HG_LURE", senderID, recipientID, "NotFriend", currentRegion);

    // Notify the recipient
    client.SendAgentAlertMessage(
        "Parental Controls: Teleport invitations are restricted to approved friends.",
        false
    );

    // Returning cleanly drops the lure.
    return;
}

        private void HandleSetHome(
            IClientAPI client,
            ulong regionHandle,
            Vector3 pos,
            Vector3 lookAt,
            uint locationID)
        {
            if (IsRestricted(client.AgentId, out _))
            {
                client.SendAgentAlertMessage(
                    "Parental Controls: Setting home is disabled.",
                    false
                );
            }
        }

        private void CheckTables()
        {

            if (string.IsNullOrEmpty(m_connectionString))
            {
                m_log.Error("[PARENTAL] Cannot check tables: Connection string is null or empty!");
                return;
            }

            m_log.WarnFormat("[PARENTAL] CheckTables: {0}", m_connectionString);

            using var conn = new MySqlConnection(m_connectionString);
            try
            {
                conn.Open();

                // 1) parental_links
                using (var cmd = new MySqlCommand(@"
                    CREATE TABLE IF NOT EXISTS `parental_links` (
                        `ChildID`      CHAR(36) NOT NULL,
                        `ParentID`     CHAR(36) NOT NULL,
                        `IsRestricted` TINYINT(1) DEFAULT 1,
                        `ParentLastSeenOffline` DATETIME NULL,
                        PRIMARY KEY (`ChildID`)
                    ) ENGINE=InnoDB;", conn))
                {
                    cmd.ExecuteNonQuery();
                }

                // 2) parental_audit (metadata-only logging)
using (var cmd = new MySqlCommand(@"
    CREATE TABLE IF NOT EXISTS `parental_audit` (
        `ID`        BIGINT NOT NULL AUTO_INCREMENT,
        `EventType` VARCHAR(64) NOT NULL,
        `SenderID`  CHAR(36) NOT NULL,
        `TargetID`  CHAR(36) NOT NULL,
        `Outcome`   VARCHAR(16) NOT NULL,
        `Reason`    VARCHAR(64) NOT NULL,
        `RegionName` VARCHAR(64) NOT NULL,
        `EventTime` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        PRIMARY KEY (`ID`),
        INDEX `idx_target_time` (`TargetID`, `EventTime`),
        INDEX `idx_sender_time` (`SenderID`, `EventTime`)
    ) ENGINE=InnoDB;", conn))
{
    cmd.ExecuteNonQuery();
}

                m_log.Info("[PARENTAL] DB tables verified: parental_links, parental_audit");
            }
            catch (Exception e)
            {
                m_log.Error("[PARENTAL] DB Error: " + e.Message, e);
            }
        }

        // Audit log - Metadata only (no content)
private void LogAudit(
    string eventType,
    UUID sender,
    UUID target,
    string reason,
    string regionName) // Added parameter
{
    try
    {
        using var conn = new MySqlConnection(m_connectionString);
        conn.Open();

        using var cmd = new MySqlCommand(@"
            INSERT INTO parental_audit
                (EventType, SenderID, TargetID, Outcome, Reason, RegionName)
            VALUES
                (@t, @s, @r, 'BLOCKED', @reason, @reg);", conn);

        cmd.Parameters.AddWithValue("@t", eventType);
        cmd.Parameters.AddWithValue("@s", sender.ToString());
        cmd.Parameters.AddWithValue("@r", target.ToString());
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.Parameters.AddWithValue("@reg", regionName); // Save the location

        cmd.ExecuteNonQuery();
    }
    catch (Exception e)
    {
        m_log.Error("[PARENTAL] Audit log failure: " + e.Message, e);
    }
}


        private void UpdateCacheForUser(UUID agentID)
        {
            using var conn = new MySqlConnection(m_connectionString);
            try
            {
                conn.Open();
                using var cmd = new MySqlCommand(@"
                    SELECT ParentID
                    FROM parental_links
                    WHERE ChildID = @cid
                      AND IsRestricted = 1;", conn);

                cmd.Parameters.AddWithValue("@cid", agentID.ToString());

                var res = cmd.ExecuteScalar();
                m_restrictedCache[agentID] =
                    (res != null) ? new UUID(res.ToString()) : UUID.Zero;
            }
            catch
            {
                // Intentionally swallow: failure just means "treat as not restricted" until next refresh
            }
        }

        private bool IsRestricted(UUID agentID, out UUID parentID)
        {
            return m_restrictedCache.TryGetValue(agentID, out parentID) && parentID != UUID.Zero;
        }

        private bool IsFriend(UUID principalID, UUID friendID)
        {
            using var conn = new MySqlConnection(m_connectionString);
            try
            {
                conn.Open();
                using var cmd = new MySqlCommand(@"
                    SELECT COUNT(*)
                    FROM Friends
                    WHERE PrincipalID = @pid
                      AND Friend      = @fid
                      AND Flags       > 0;", conn);

                /*
                    Flags:
                    0       Pending (request sent, not accepted)
                    1       Can see online status
                    2       Can see map
                    4       Can modify objects
                    7       Fully accepted (1 + 2 + 4)
                */

                cmd.Parameters.AddWithValue("@pid", principalID.ToString());
                cmd.Parameters.AddWithValue("@fid", friendID.ToString());

                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch
            {
                return false;
            }
        }

        private void StartHttpListener()
        {
            try
            {
                m_httpListener = new HttpListener();
                m_httpListener.Prefixes.Add($"http://{m_listenerIP}:{m_adminPort}/flush/");
                m_httpListener.Start();
                m_httpListener.BeginGetContext(OnHttpRequest, m_httpListener);
            }
            catch (Exception e)
            {
                m_log.Error("[PARENTAL] Listener Error: " + e.Message, e);
            }
        }


private void OnHttpRequest(IAsyncResult result)
{
    if (!m_httpListener.IsListening) return;

    try
    {
        var context = m_httpListener.EndGetContext(result);
        string path = context.Request.Url.AbsolutePath.ToLower();
        string target = context.Request.QueryString["uuid"];

        if (!string.IsNullOrEmpty(target) && UUID.TryParse(target, out UUID agentID))
        {
            if (path.Contains("/flush/"))
            {
                UpdateCacheForUser(agentID);
                m_log.InfoFormat("[PARENTAL] Flushed cache for {0}", target);
            }
            else if (path.Contains("/warn/"))
            {
                SendParentalWarning(agentID);
            }
            else if (path.Contains("/kick/"))
            {
                KickChild(agentID);
            }
        }

        context.Response.StatusCode = 200;
        context.Response.Close();
        m_httpListener.BeginGetContext(OnHttpRequest, m_httpListener);
    }
    catch (Exception e)
    {
        m_log.Error("[PARENTAL] HTTP Error: " + e.Message);
    }
}

private void SendParentalWarning(UUID agentID)
{
    Scene scene = GetSceneByUUID(agentID);
    if (scene != null && scene.TryGetScenePresence(agentID, out ScenePresence presence))
    {
        presence.ControllingClient.SendAgentAlertMessage(
            "Parental Warning: Your parent has been offline. Please relog or you will be disconnected in 60 seconds.", 
            true); // true = modal popup
        m_log.WarnFormat("[PARENTAL] Warning sent to unattended child: {0}", agentID);
    }
}

private void KickChild(UUID agentID)
{
    Scene scene = GetSceneByUUID(agentID);
    if (scene != null)
    {
        // Log the audit before closing the connection
        LogAudit("AUTO_KICK", agentID, UUID.Zero, "ParentOfflineTimeout", scene.RegionInfo.RegionName);
        
        // Disconnect the user
        scene.CloseAgent(agentID, true);
        m_log.ErrorFormat("[PARENTAL] Kicked unattended child after grace period: {0}", agentID);
    }
}

    }
}

