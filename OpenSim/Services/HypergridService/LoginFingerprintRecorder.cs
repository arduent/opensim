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
using System;
using MySql.Data.MySqlClient;

namespace OpenSim.Services.HypergridService
{
    public static class LoginFingerprintRecorder
    {
        private static string _connStr = string.Empty;

        public static void Init(string connStr)
        {
            _connStr = connStr ?? string.Empty;
        }

        public static void Record(
            string agentId,
            string firstName,
            string lastName,
            string ip,
            string mac,
            string id0,
            string homeUri)
        {
            if (string.IsNullOrEmpty(_connStr))
                return;

            try
            {
                using var conn = new MySqlConnection(_connStr);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO user_login_fingerprints
                    (agent_uuid, first_name, last_name, ip_address, mac, id0, home_uri)
                    VALUES
                    (@agent, @fn, @ln, @ip, @mac, @id0, @home)";

                cmd.Parameters.AddWithValue("@agent", agentId);
                cmd.Parameters.AddWithValue("@fn", firstName);
                cmd.Parameters.AddWithValue("@ln", lastName);
                cmd.Parameters.AddWithValue("@ip", ip);
                cmd.Parameters.AddWithValue("@mac", mac);
                cmd.Parameters.AddWithValue("@id0", id0);
                cmd.Parameters.AddWithValue("@home", homeUri);

                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Console.WriteLine("[FingerprintRecorder] " + e.Message);
            }
        }
    }
}

