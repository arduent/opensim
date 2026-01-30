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


/*
 * Robust.HG.ini modifications:
 * [Messaging]
 *   OfflineIMService = ParentalControlsRobust.dll:ParentalOfflineIMService
 *
 * [Groups]
 *   OfflineIMService = ParentalControlsRobust.dll:ParentalOfflineIMService
 */

using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using log4net;

namespace OpenSim.Services.ParentalControls
{
    public class ParentalOfflineIMService : IOfflineIMService
    {
        private static readonly ILog m_log = LogManager.GetLogger("ParentalOfflineIMService");

        private readonly IOfflineIMService m_inner;
        private readonly string m_connectionString;

        public ParentalOfflineIMService(IOfflineIMService inner, IConfigSource config)
        {
            m_inner = inner;

            IConfig db = config.Configs["DatabaseService"];
            if (db == null)
                throw new Exception("[PARENTAL] DatabaseService config missing");

            m_connectionString = db.GetString("ConnectionString");
        }

        public bool StoreMessage(GridInstantMessage im, out string reason)
        {
            reason = string.Empty;

            UUID recipient = new UUID(im.toAgentID);

            if (IsProtectedChild(recipient))
            {
                m_log.WarnFormat("[PARENTAL][ROBUST] BLOCKED OFFLINE IM {0} -> {1}", im.fromAgentID, im.toAgentID);
                reason = "Parental Controls: Offline messages are disabled for this account.";
                return false;
            }

            return m_inner.StoreMessage(im, out reason);
        }

        public List<GridInstantMessage> GetMessages(UUID principalID) => m_inner.GetMessages(principalID);

        public void DeleteMessages(UUID principalID) => m_inner.DeleteMessages(principalID);

        private bool IsProtectedChild(UUID childID)
        {
            using var conn = new MySqlConnection(m_connectionString);
            conn.Open();

            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM parental_links WHERE ChildID=@c AND IsRestricted=1",
                conn
            );

            cmd.Parameters.AddWithValue("@c", childID.ToString());
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }
}

