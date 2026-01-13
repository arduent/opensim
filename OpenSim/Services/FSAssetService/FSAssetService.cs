/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace OpenSim.Services.FSAssetService
{
    public class FSAssetConnector : ServiceBase, IAssetService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();

        static byte[] ToCString(string s)
        {
            byte[] ret = enc.GetBytes(s);
            Array.Resize(ref ret, ret.Length + 1);
            ret[ret.Length - 1] = 0;

            return ret;
        }

        protected IAssetLoader m_AssetLoader = null;
        protected IFSAssetDataPlugin m_DataConnector = null;
        protected IAssetService m_FallbackService;
        protected Thread m_WriterThread;
        protected Thread m_StatsThread;
        protected string m_SpoolDirectory;
        protected int m_WriteSleepMs;
        protected object m_readLock = new object();
        protected object m_statsLock = new object();
        protected int m_readCount = 0;
        protected int m_readTicks = 0;
        protected int m_missingAssets = 0;
        protected int m_missingAssetsFS = 0;
        protected string m_FSBase;
        protected bool m_useOsgridFormat = false;
        protected bool m_showStats = true;

        private static bool m_mainInitialized;
        private static object m_initLock = new object();

        private bool m_isMainInstance;

        public FSAssetConnector(IConfigSource config)
            : this(config, "AssetService")
        {
        }

        public FSAssetConnector(IConfigSource config, string configName) : base(config)
        {
            IConfig assetConfig = config.Configs[configName];
            if (assetConfig == null)
                throw new Exception("No AssetService configuration");

            lock (m_initLock)
            {
                if (!m_mainInitialized)
                {
                    m_mainInitialized = true;
                    m_isMainInstance = !assetConfig.GetBoolean("SecondaryInstance", false);

                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "show assets", "show assets", "Show asset stats",
                            HandleShowAssets);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "show digest", "show digest <ID>", "Show asset digest",
                            HandleShowDigest);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "delete asset", "delete asset <ID>",
                            "Delete asset from database",
                            HandleDeleteAsset);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "import", "import <conn> <table> [<start> <count>]",
                            "Import legacy assets",
                            HandleImportAssets);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "force import", "force import <conn> <table> [<start> <count>]",
                            "Import legacy assets, overwriting current content",
                            HandleImportAssets);
                }
                else
                {
                    m_isMainInstance = false; // yes redundant...
                }
            }

            // Get Database Connector from Asset Config (If present)
            string dllName = assetConfig.GetString("StorageProvider", string.Empty);
            string connectionString = assetConfig.GetString("ConnectionString", string.Empty);
            string realm = assetConfig.GetString("Realm", "fsassets");

            int SkipAccessTimeDays = assetConfig.GetInt("DaysBetweenAccessTimeUpdates", 0);

            // If not found above, fallback to Database defaults
            IConfig dbConfig = config.Configs["DatabaseService"];

            if (dbConfig != null)
            {
                if (dllName.Length == 0)
                    dllName = dbConfig.GetString("StorageProvider", String.Empty);

                if (connectionString.Length == 0)
                    connectionString = dbConfig.GetString("ConnectionString", String.Empty);
            }

            // No databse connection found in either config
            if (string.IsNullOrEmpty(dllName))
                throw new Exception("No StorageProvider configured");

            if (string.IsNullOrEmpty(connectionString))
                throw new Exception("Missing database connection string");

            // Create Storage Provider
            m_DataConnector = LoadPlugin<IFSAssetDataPlugin>(dllName);

            if (m_DataConnector == null)
                throw new Exception(string.Format("Could not find a storage interface in the module {0}", dllName));

            // Initialize DB And perform any migrations required
            m_log.InfoFormat("[FSASSETS]: Connecting to: {0}",connectionString);
            m_DataConnector.Initialise(connectionString, realm, SkipAccessTimeDays);

            // Setup Fallback Service
            string str = assetConfig.GetString("FallbackService", string.Empty);

            if (str.Length > 0)
            {
                object[] args = new object[] { config };
                m_FallbackService = LoadPlugin<IAssetService>(str, args);
                if (m_FallbackService != null)
                {
                    m_log.Info("[FSASSETS]: Fallback service loaded");
                }
                else
                {
                    m_log.Error("[FSASSETS]: Failed to load fallback service");
                }
            }

            // Setup directory structure including temp directory
            m_SpoolDirectory = assetConfig.GetString("SpoolDirectory", "/tmp");

            string spoolTmp = Path.Combine(m_SpoolDirectory, "spool");

            Directory.CreateDirectory(spoolTmp);

            m_FSBase = assetConfig.GetString("BaseDirectory", String.Empty);
            if (m_FSBase.Length == 0)
            {
                m_log.ErrorFormat("[FSASSETS]: BaseDirectory not specified");
                throw new Exception("Configuration error");
            }

            // get write delay default = 1 sec
            m_WriteSleepMs = assetConfig.GetInt("WriteSleepMs", 1000);

            m_useOsgridFormat = assetConfig.GetBoolean("UseOsgridFormat", m_useOsgridFormat);

            // Default is to show stats to retain original behaviour
            m_showStats = assetConfig.GetBoolean("ShowConsoleStats", m_showStats);

            if (m_isMainInstance)
            {
                string loader = assetConfig.GetString("DefaultAssetLoader", string.Empty);
                if (loader.Length > 0)
                {
                    m_AssetLoader = LoadPlugin<IAssetLoader>(loader);
                    string loaderArgs = assetConfig.GetString("AssetLoaderArgs", string.Empty);
                    m_log.InfoFormat("[FSASSETS]: Loading default asset set from {0}", loaderArgs);
                    m_AssetLoader.ForEachDefaultXmlAsset(loaderArgs,
                            delegate(AssetBase a)
                            {
                                Store(a, false);
                            });
                }

                if(m_WriterThread == null)
                {
                    m_WriterThread = new Thread(Writer);
                    m_WriterThread.Start();
                }

                if (m_showStats && m_StatsThread == null)
                {
                    m_StatsThread = new Thread(Stats);
                    m_StatsThread.Start();
                }
            }

            m_log.Info("[FSASSETS]: FS asset service enabled");
        }

        private void Stats()
        {
            while (true)
            {
                Thread.Sleep(60000);

                lock (m_statsLock)
                {
                    if (m_readCount > 0)
                    {
                        double avg = (double)m_readTicks / (double)m_readCount;
//                        if (avg > 10000)
//                            Environment.Exit(0);
                        m_log.InfoFormat("[FSASSETS]: Read stats: {0} files, {1} ticks, avg {2:F2}, missing {3}, FS {4}", m_readCount, m_readTicks, (double)m_readTicks / (double)m_readCount, m_missingAssets, m_missingAssetsFS);
                    }
                    m_readCount = 0;
                    m_readTicks = 0;
                    m_missingAssets = 0;
                    m_missingAssetsFS = 0;
                }
            }
        }

	private void Writer()
	{
	    string spoolSubDir = Path.Combine(m_SpoolDirectory, "spool");
	    m_log.InfoFormat("[FSASSETS]: High-Speed Writer started. Subfolder: {0}", spoolSubDir);
	
	    while (true)
	    {
	        try
	        {
	            // Only look for .asset files in the /spool/ subfolder
	            string[] files = Directory.GetFiles(spoolSubDir, "*.asset");
	
	            if (files.Length > 0)
	            {
	                int tickCount = Environment.TickCount;
	                int batchSize = Math.Min(files.Length, 100); 
	                
	                for (int i = 0; i < batchSize; i++)
	                {
	                    string currentFile = files[i];
	                    string hash = Path.GetFileNameWithoutExtension(currentFile);
	                    string s = HashToFile(hash);
	                    string diskFile = Path.Combine(m_FSBase, s);
	                    bool pathOk = false;
	
	                    // 1. Ensure Directory Exists
	                    try
	                    {
	                        Directory.CreateDirectory(Path.GetDirectoryName(diskFile));
	                        pathOk = true;
	                    }
	                    catch (Exception ex)
	                    {
	                        m_log.ErrorFormat("[FSASSETS]: Failed to create directory for {0}: {1}", diskFile, ex.Message);
	                    }
	
	                    if (pathOk)
	                    {
	                        try
	                        {
	                            // 2. Read and Compress
	                            byte[] data = File.ReadAllBytes(currentFile);
	                            using (FileStream fs = new FileStream(diskFile + ".gz", FileMode.Create, FileAccess.Write, FileShare.None))
	                            {
	                                using (GZipStream gz = new GZipStream(fs, CompressionMode.Compress))
	                                {
	                                    gz.Write(data, 0, data.Length);
	                                }
	                            }
	
	                            // 3. CRITICAL: Delete the file from the spool immediately
	                            File.Delete(currentFile);
	                        }
	                        catch (Exception ex)
	                        {
	                            // If we can't delete it, we MUST skip it or we loop forever
	                            m_log.ErrorFormat("[FSASSETS]: Failed to process/delete {0}: {1}", currentFile, ex.Message);
	                            
	                            // Rename the file to .failed so the loop stops seeing it as a .asset
	                            try { File.Move(currentFile, currentFile + ".failed"); } catch {}
	                        }
	                    }
	                }
	
	                int totalTicks = Environment.TickCount - tickCount;
	                //m_log.InfoFormat("[FSASSETS]: Write cycle complete, {0} files processed in {1}ms", batchSize, totalTicks);
	
	                // If backlog exists, skip sleep
	                if (files.Length > batchSize) continue; 
	            }
	        }
	        catch (Exception e)
	        {
	            m_log.ErrorFormat("[FSASSETS]: Writer loop catastrophic error: {0}", e.Message);
	        }
	
	        Thread.Sleep(m_WriteSleepMs);
	    }
	}

        string GetSHA256Hash(byte[] data)
        {
            return Util.SHA256Hash(data);
        }

        public string HashToPath(string hash)
        {
            if (hash == null || hash.Length < 10)
                return "junkyard";

            if (m_useOsgridFormat)
            {
                /*
                 * The code below is the OSGrid code.
                 */
                return Path.Combine(hash.Substring(0, 3),
                       Path.Combine(hash.Substring(3, 3)));
            }
            else
            {
                /*
                 * The below is what core would normally use.
                 * This is modified to work in OSGrid, as seen
                 * above, because the SRAS data is structured
                 * that way.
                 */
                return Path.Combine(hash.Substring(0, 2),
                       Path.Combine(hash.Substring(2, 2),
                       Path.Combine(hash.Substring(4, 2),
                       hash.Substring(6, 4))));
            }
        }

        private bool AssetExists(string hash)
        {
            string s = HashToFile(hash);
            string diskFile = Path.Combine(m_FSBase, s);

            if (File.Exists(diskFile + ".gz") || File.Exists(diskFile))
                return true;

            return false;
        }

        public virtual bool[] AssetsExist(string[] ids)
        {
            UUID[] uuid = Array.ConvertAll(ids, id => UUID.Parse(id));
            return m_DataConnector.AssetsExist(uuid);
        }

        public string HashToFile(string hash)
        {
            return Path.Combine(HashToPath(hash), hash);
        }

        public virtual AssetBase Get(string id)
        {
            string hash;

            return Get(id, out hash);
        }

        public AssetBase Get(string id, string ForeignAssetService, bool dummy)
        {
            return null;
        }

        private AssetBase Get(string id, out string sha)
        {
            string hash = string.Empty;

            int startTime = System.Environment.TickCount;
            AssetMetadata metadata;

            lock (m_readLock)
            {
                metadata = m_DataConnector.Get(id, out hash);
            }

            sha = hash;

            if (metadata == null)
            {
                AssetBase asset = null;
                if (m_FallbackService != null)
                {
                    asset = m_FallbackService.Get(id);
                    if (asset != null)
                    {
                        asset.Metadata.ContentType =
                                SLUtil.SLAssetTypeToContentType((int)asset.Type);
                        sha = GetSHA256Hash(asset.Data);
                        m_log.InfoFormat("[FSASSETS]: Added asset {0} from fallback to local store", id);
                        Store(asset);
                    }
                }
                if (asset == null && m_showStats)
                {
                    // m_log.InfoFormat("[FSASSETS]: Asset {0} not found", id);
                    m_missingAssets++;
                }
                return asset;
            }
            AssetBase newAsset = new AssetBase();
            newAsset.Metadata = metadata;
            try
            {
                newAsset.Data = GetFsData(hash);
                if (newAsset.Data.Length == 0)
                {
                    AssetBase asset = null;
                    if (m_FallbackService != null)
                    {
                        asset = m_FallbackService.Get(id);
                        if (asset != null)
                        {
                            asset.Metadata.ContentType =
                                    SLUtil.SLAssetTypeToContentType((int)asset.Type);
                            sha = GetSHA256Hash(asset.Data);
                            m_log.InfoFormat("[FSASSETS]: Added asset {0} from fallback to local store", id);
                            Store(asset);
                        }
                    }
                    if (asset == null)
                    {
                        if (m_showStats)
                            m_missingAssetsFS++;
                        // m_log.InfoFormat("[FSASSETS]: Asset {0}, hash {1} not found in FS", id, hash);
                    }
                    else
                    {
                        // Deal with bug introduced in Oct. 20 (1eb3e6cc43e2a7b4053bc1185c7c88e22356c5e8)
                        // Fix bad assets before sending them elsewhere
                        if (asset.Type == (int)AssetType.Object && asset.Data != null)
                        {
                            string xml = ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(asset.Data));
                            asset.Data = Utils.StringToBytes(xml);
                        }
                        return asset;
                    }
                }

                if (m_showStats)
                {
                    lock (m_statsLock)
                    {
                        m_readTicks += Environment.TickCount - startTime;
                        m_readCount++;
                    }
                }

                // Deal with bug introduced in Oct. 20 (1eb3e6cc43e2a7b4053bc1185c7c88e22356c5e8)
                // Fix bad assets before sending them elsewhere
                if (newAsset.Type == (int)AssetType.Object && newAsset.Data != null)
                {
                    string xml = ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(newAsset.Data));
                    newAsset.Data = Utils.StringToBytes(xml);
                }

                return newAsset;
            }
            catch (Exception exception)
            {
		m_log.ErrorFormat("[FSASSETS]: Database connection error during Get: {0}", exception.Message);
		    // Return null so OpenSim treats it as a 'missing asset' and stays alive
		return null;
            }
        }

        public virtual AssetMetadata GetMetadata(string id)
        {
            string hash;
            return m_DataConnector.Get(id, out hash);
        }

        public virtual byte[] GetData(string id)
        {
            string hash;
            if (m_DataConnector.Get(id, out hash) == null)
                return null;

            return GetFsData(hash);
        }

        public bool Get(string id, Object sender, AssetRetrieved handler)
        {
            AssetBase asset = Get(id);

            handler(id, sender, asset);

            return true;
        }

        public byte[] GetFsData(string hash)
        {
            string spoolFile = Path.Combine(m_SpoolDirectory, hash + ".asset");

            if (File.Exists(spoolFile))
            {
                try
                {
                    byte[] content = File.ReadAllBytes(spoolFile);

                    return content;
                }
                catch
                {
                }
            }

            string file = HashToFile(hash);
            string diskFile = Path.Combine(m_FSBase, file);

            if (File.Exists(diskFile + ".gz"))
            {
                try
                {
                    using (GZipStream gz = new GZipStream(new FileStream(diskFile + ".gz", FileMode.Open, FileAccess.Read), CompressionMode.Decompress))
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] data = new byte[32768];
                            int bytesRead;

                            do
                            {
                                bytesRead = gz.Read(data, 0, 32768);
                                if (bytesRead > 0)
                                    ms.Write(data, 0, bytesRead);
                            } while (bytesRead > 0);

                            return ms.ToArray();
                        }
                    }
                }
                catch (Exception)
                {
                    return Array.Empty<byte>();
                }
            }
            else if (File.Exists(diskFile))
            {
                try
                {
                    byte[] content = File.ReadAllBytes(diskFile);

                    return content;
                }
                catch
                {
                }
            }
            return Array.Empty<byte>();

        }

        public virtual string Store(AssetBase asset)
        {
            return Store(asset, false);
        }

	private string Store(AssetBase asset, bool force)
	{
	    // Generate the hash (necessary to know if we already have it)
	    string hash = GetSHA256Hash(asset.Data);

	    // Sanitize Metadata (standard OpenSim length checks)
	    if (asset.Name.Length > AssetBase.MAX_ASSET_NAME)
	        asset.Name = asset.Name.Substring(0, AssetBase.MAX_ASSET_NAME);
	    if (asset.Description.Length > AssetBase.MAX_ASSET_DESC)
	        asset.Description = asset.Description.Substring(0, AssetBase.MAX_ASSET_DESC);
	
	    // Handle IDs
	    if (asset.ID.Length == 0)
	    {
	        if (asset.FullID.IsZero()) asset.FullID = UUID.Random();
	        asset.ID = asset.FullID.ToString();
	    }

            string spoolSubDir = Path.Combine(m_SpoolDirectory, "spool");
            string tempFile = Path.Combine(spoolSubDir, hash + ".tmp");
            string finalFile = Path.Combine(spoolSubDir, hash + ".asset");
	
	    // FIRE AND FORGET: Save to RAM Spool and return immediately
	    // if (!AssetExists(hash)) // this hits the disk
            if (!File.Exists(finalFile)) // hits ram drive
	    {

	        if (!File.Exists(finalFile))
	        {
	            // Sanitize XML for objects
	            if (asset.Type == (int)AssetType.Object && asset.Data != null)
	            {
	                string xml = ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(asset.Data));
	                asset.Data = Utils.StringToBytes(xml);
	            }
	
	            // Write to RAM disk - very fast
	            using (FileStream fs = File.Create(tempFile))
	            {
	                fs.Write(asset.Data, 0, asset.Data.Length);
	            }
	            File.Move(tempFile, finalFile);
	        }
	    }
	
	    // ASYNC DATABASE: Queue the DB write so we don't block the HTTP response
	    // We use a background thread to tell the DB about the asset metadata
/*
	    System.Threading.Tasks.Task.Run(() => {
	        try {
	            m_DataConnector.Store(asset.Metadata, hash);
	        } catch (Exception e) {
	            m_log.ErrorFormat("[FSASSETS]: Async DB Write failed for {0}: {1}", asset.ID, e.Message);
	        }
	    });
*/

// DATABASE WRITE: Removed Task.Run to prevent thread pool exhaustion/flood at startup
try 
{
    m_DataConnector.Store(asset.Metadata, hash);
} 
catch (Exception e) 
{
    m_log.ErrorFormat("[FSASSETS]: Database write failed for {0}: {1}", asset.ID, e.Message);
}
	
	    // IMMEDIATELY return the ID to the remote grid
	    return asset.ID;
	}

        public bool UpdateContent(string id, byte[] data)
        {
            return false;

//            string oldhash;
//            AssetMetadata meta = m_DataConnector.Get(id, out oldhash);
//
//            if (meta == null)
//                return false;
//
//            AssetBase asset = new AssetBase();
//            asset.Metadata = meta;
//            asset.Data = data;
//
//            Store(asset);
//
//            return true;
        }

        public virtual bool Delete(string id)
        {
            m_DataConnector.Delete(id);

            return true;
        }

        private void HandleShowAssets(string module, string[] args)
        {
            int num = m_DataConnector.Count();
            MainConsole.Instance.Output(string.Format("Total asset count: {0}", num));
        }

        private void HandleShowDigest(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: show digest <ID>");
                return;
            }

            string hash;
            AssetBase asset = Get(args[2], out hash);

            if (asset == null || asset.Data.Length == 0)
            {
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            int i;

            MainConsole.Instance.Output(String.Format("Name: {0}", asset.Name));
            MainConsole.Instance.Output(String.Format("Description: {0}", asset.Description));
            MainConsole.Instance.Output(String.Format("Type: {0}", asset.Type));
            MainConsole.Instance.Output(String.Format("Content-type: {0}", asset.Metadata.ContentType));
            MainConsole.Instance.Output(String.Format("Flags: {0}", asset.Metadata.Flags.ToString()));
            MainConsole.Instance.Output(String.Format("FS file: {0}", HashToFile(hash)));

            for (i = 0 ; i < 5 ; i++)
            {
                int off = i * 16;
                if (asset.Data.Length <= off)
                    break;
                int len = 16;
                if (asset.Data.Length < off + len)
                    len = asset.Data.Length - off;

                byte[] line = new byte[len];
                Array.Copy(asset.Data, off, line, 0, len);

                string text = BitConverter.ToString(line);
                MainConsole.Instance.Output(String.Format("{0:x4}: {1}", off, text));
            }
        }

        private void HandleDeleteAsset(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: delete asset <ID>");
                return;
            }

            AssetBase asset = Get(args[2]);

            if (asset == null || asset.Data.Length == 0)
            {
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            m_DataConnector.Delete(args[2]);

            MainConsole.Instance.Output("Asset deleted");
        }

        private void HandleImportAssets(string module, string[] args)
        {
            bool force = false;
            if (args[0] == "force")
            {
                force = true;
                List<string> list = new List<string>(args);
                list.RemoveAt(0);
                args = list.ToArray();
            }
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: import <conn> <table> [<start> <count>]");
            }
            else
            {
                string conn = args[1];
                string table = args[2];
                int start = 0;
                int count = -1;
                if (args.Length > 3)
                {
                    start = Convert.ToInt32(args[3]);
                }
                if (args.Length > 4)
                {
                    count = Convert.ToInt32(args[4]);
                }
                m_DataConnector.Import(conn, table, start, count, force, new FSStoreDelegate(Store));
            }
        }

        public AssetBase GetCached(string id)
        {
            return Get(id);
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            return;
        }
    }
}
