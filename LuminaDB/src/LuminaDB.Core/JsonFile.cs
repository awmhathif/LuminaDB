using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LuminaDB.Core
{
    /// <summary>Small JSON files (state, PID records). Writes are atomic so a power cut never leaves half a file.</summary>
    public static class JsonFile
    {
        public static T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(fs);
            }
            catch (SerializationException) { return null; }   // corrupt file: treat as missing
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                File.WriteAllBytes(tmp, ms.ToArray());
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Written next to a running server so a restarted LuminaDB can adopt it instead of starting a second one.</summary>
    [DataContract]
    public sealed class ServerRecord
    {
        [DataMember] public int ProcessId { get; set; }
        [DataMember] public string ExePath { get; set; }
        [DataMember] public int Port { get; set; }
        [DataMember] public string DataDir { get; set; }
        [DataMember] public long StartedUtcTicks { get; set; }
    }

    /// <summary>Stamped into each data folder: which engine set created it (SPEC Â§3).</summary>
    [DataContract]
    public sealed class DataFolderStamp
    {
        public const string FileName = "lumina-engine.json";
        [DataMember] public string EngineSet { get; set; }
        [DataMember] public string CreatedByVersion { get; set; }
        [DataMember] public long CreatedUtcTicks { get; set; }
    }

    /// <summary>Things remembered between runs (last ports, so project URLs stay the same).</summary>
    [DataContract]
    public sealed class LuminaState
    {
        [DataMember] public int? MariaDbPortModern { get; set; }
        [DataMember] public int? MariaDbPortLegacy { get; set; }
        [DataMember] public int? MariaDbPortX86 { get; set; }
        /// <summary>Web ports kept between runs so project URLs stay the same (SPEC §4).</summary>
        [DataMember] public int? WebPort { get; set; }
        [DataMember] public int? PhpPort { get; set; }
        [DataMember] public int? ApachePort { get; set; }
        [DataMember] public int? CaddyAdminPort { get; set; }
        [DataMember] public int? MailPort { get; set; }

        public int? GetMariaDbPort(EngineSetKind k) =>
            k == EngineSetKind.Modern ? MariaDbPortModern : k == EngineSetKind.Legacy ? MariaDbPortLegacy : MariaDbPortX86;

        public void SetMariaDbPort(EngineSetKind k, int port)
        {
            if (k == EngineSetKind.Modern) MariaDbPortModern = port;
            else if (k == EngineSetKind.Legacy) MariaDbPortLegacy = port;
            else MariaDbPortX86 = port;
        }

        public void ClearMariaDbPort(EngineSetKind k)
        {
            if (k == EngineSetKind.Modern) MariaDbPortModern = null;
            else if (k == EngineSetKind.Legacy) MariaDbPortLegacy = null;
            else MariaDbPortX86 = null;
        }

        public static LuminaState Load(AppPaths p) => JsonFile.Read<LuminaState>(p.StateFile) ?? new LuminaState();
        public void Save(AppPaths p) => JsonFile.Write(p.StateFile, this);
    }
}
