/*
CREATE TABLE user_login_fingerprints (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    agent_uuid CHAR(36) NOT NULL,
    first_name VARCHAR(64),
    last_name VARCHAR(64),
    ip_address VARCHAR(45),
    mac VARCHAR(64),
    id0 VARCHAR(64),
    home_uri VARCHAR(255),
    login_time DATETIME DEFAULT CURRENT_TIMESTAMP,

    KEY idx_agent (agent_uuid),
    KEY idx_mac (mac),
    KEY idx_id0 (id0),
    KEY idx_ip (ip_address)
);
*/
using System.Collections.Generic;
using OpenSim.Data;

namespace OpenSim.Services.HypergridService
{
    public static class LoginFingerprintRecorder
    {
        public static void Record(
            IGenericData db,
            string agentId,
            string firstName,
            string lastName,
            string ip,
            string mac,
            string id0,
            string homeUri)
        {
            if (db == null) return;

            string sql = @"
                INSERT INTO user_login_fingerprints
                (agent_uuid, first_name, last_name, ip_address, mac, id0, home_uri)
                VALUES (?agent, ?fn, ?ln, ?ip, ?mac, ?id0, ?home)
            ";

            var parameters = new Dictionary<string, object>
            {
                ["?agent"] = agentId,
                ["?fn"] = firstName,
                ["?ln"] = lastName,
                ["?ip"] = ip,
                ["?mac"] = mac,
                ["?id0"] = id0,
                ["?home"] = homeUri
            };

            db.ExecuteNonQuery(sql, parameters);
        }
    }
}


