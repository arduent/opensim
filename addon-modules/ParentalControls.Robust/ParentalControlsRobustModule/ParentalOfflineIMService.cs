using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using log4net;

namespace OpenSim.Services.ParentalControls
{
    public class ParentalOfflineIMService : IOfflineIMService
    {
        private static readonly ILog m_log =
            LogManager.GetLogger("ParentalOfflineIMService");

        private readonly IOfflineIMService m_inner;
        private readonly string m_connectionString;

        // Cache: UUID -> (restricted, expiration time)
        private readonly ConcurrentDictionary<UUID, (bool restricted, DateTime expires)>
            _restrictionCache = new();

        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

        public ParentalOfflineIMService(IConfigSource config)
        {
            IConfig db = config.Configs["DatabaseService"];
            if (db == null)
                throw new Exception("[PARENTAL] DatabaseService config missing");

            m_connectionString = db.GetString("ConnectionString");

            // IMPORTANT:
            // Explicitly load the ORIGINAL OfflineIMService
            // Do NOT load whatever is configured in [Messaging]
            m_inner = ServerUtils.LoadPlugin<IOfflineIMService>(
                "OpenSim.Addons.OfflineIM.dll:OfflineIMService",
                new object[] { config }
            );

            m_log.Info("[PARENTAL][ROBUST] ParentalOfflineIMService initialized.");
        }

        public bool StoreMessage(GridInstantMessage im, out string reason)
        {
            reason = string.Empty;

            UUID recipient = new UUID(im.toAgentID);

            if (IsProtectedChild(recipient))
            {
                m_log.WarnFormat(
                    "[PARENTAL][ROBUST] BLOCKED OFFLINE IM {0} -> {1}",
                    im.fromAgentID, im.toAgentID);

                reason = "Parental Controls: Offline messages are disabled for this account.";
                return false;
            }

            return m_inner.StoreMessage(im, out reason);
        }

        public List<GridInstantMessage> GetMessages(UUID principalID)
            => m_inner.GetMessages(principalID);

        public void DeleteMessages(UUID principalID)
            => m_inner.DeleteMessages(principalID);

        private bool IsProtectedChild(UUID childID)
        {
            // Check cache first
            if (_restrictionCache.TryGetValue(childID, out var entry))
            {
                if (DateTime.UtcNow < entry.expires)
                    return entry.restricted;
            }

            bool restricted = false;

            try
            {
                using var conn = new MySqlConnection(m_connectionString);
                conn.Open();

                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM parental_links " +
                    "WHERE ChildID=@c AND IsRestricted=1 LIMIT 1",
                    conn);

                cmd.Parameters.AddWithValue("@c", childID.ToString());

                using var reader = cmd.ExecuteReader();
                restricted = reader.Read();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat(
                    "[PARENTAL][ROBUST] DB error while checking restriction: {0}",
                    ex.Message);
            }

            // Update cache
            _restrictionCache[childID] =
                (restricted, DateTime.UtcNow.Add(CacheDuration));

            return restricted;
        }
    }
}

