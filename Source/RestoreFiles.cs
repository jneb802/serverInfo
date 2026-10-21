using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerInfo
{
    // Kept independent of Unity for deterministic file transaction tests.
    internal static class RestoreFiles
    {
        private const int MaximumBytes = 8 * 1024 * 1024;

        internal static void Process(string root, string operation, string world,
            Action<string, string> prepare, Action<string, string> invalidate)
        {
            SafePath(root, "DashboardRestores", Path.GetFileName(operation));
            string pending = SafePath(operation, "pending");
            string processing = SafePath(operation, "processing");
            File.Move(pending, processing); // Claim once. Never replay after a crash.
            Result result = new Result();
            try
            {
                Request request = ReadJson<Request>(SafePath(operation, "request.json"));
                if (request.expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    throw new InvalidOperationException("The confirmation expired. Select the backup again");
                if (request.world != world) throw new InvalidOperationException("The active world does not match the selected backup");
                if (!Regex.IsMatch(request.account, "^[0-9]{17}$")) throw new InvalidOperationException("Invalid Steam account");
                string target = SafePath(root, "Characters", request.account, Component(request.character) + ".yaml");
                byte[] backup = ReadBounded(SafePath(operation, "backup.yaml"));
                if (Hash(backup) != request.backup_sha256) throw new InvalidOperationException("The selected backup changed");
                prepare(request.account, request.character);
                byte[] current = ReadBounded(target);
                if (Hash(current) != request.before_sha256)
                    throw new InvalidOperationException("The current file changed after selection. Select the backup again");
                // Client recovery snapshots must be older than this deliberate
                // restoration. All other YAML bytes are kept as selected.
                byte[] restored = AdvanceSequence(backup, current);
                string temporary = SafePath(Path.GetDirectoryName(target)!, ".dashboard-" + Path.GetFileName(operation));
                try
                {
                    WriteNew(temporary, restored);
                    File.Replace(temporary, target, SafePath(operation, "before.yaml"));
                    invalidate(request.account, request.character);
                    if (Hash(ReadBounded(target)) != Hash(restored))
                        throw new InvalidOperationException("Replacement verification failed; ask an operator to inspect this restore");
                    result.success = true;
                    result.message = "Restored and verified. The player can reconnect";
                    result.sha256 = Hash(restored);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (Exception exception)
            {
                // Explicit validation errors are safe for the moderator. Other
                // errors may contain paths; retain only their type in the reply.
                result.message = exception is InvalidOperationException ? exception.Message
                    : "Restore could not be completed (" + exception.GetType().Name + "). Ask an operator to inspect the operation";
            }
            string resultPath = SafePath(operation, "result.json");
            string resultTemp = SafePath(operation, "result.tmp");
            using (MemoryStream buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Result)).WriteObject(buffer, result);
                WriteNew(resultTemp, buffer.ToArray());
            }
            File.Move(resultTemp, resultPath);
        }

        internal static byte[] AdvanceSequence(byte[] backup, byte[] current)
        {
            UTF8Encoding encoding = new UTF8Encoding(false, true);
            string text = encoding.GetString(backup);
            long next = checked(Math.Max(Sequence(text), Sequence(encoding.GetString(current))) + 1);
            string replacement = "saveSequence: " + next.ToString(CultureInfo.InvariantCulture);
            if (Regex.IsMatch(text, "^saveSequence:", RegexOptions.Multiline))
                text = Regex.Replace(text, "^saveSequence:[^\\r\\n]*", replacement, RegexOptions.Multiline);
            else text = replacement + "\n" + text;
            return encoding.GetBytes(text);
        }

        private static long Sequence(string text)
        {
            MatchCollection matches = Regex.Matches(text, "^saveSequence: *([0-9]+) *\\r?$", RegexOptions.Multiline);
            if (matches.Count > 1 || (matches.Count == 0 && Regex.IsMatch(text, "^saveSequence:", RegexOptions.Multiline)))
                throw new InvalidOperationException("Invalid recovery sequence in character file");
            return matches.Count == 0 ? 0 : long.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static string Component(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\', '\0', '\r', '\n' }) >= 0)
                throw new InvalidOperationException("Invalid character path");
            return name;
        }

        private static string SafePath(string root, params string[] parts)
        {
            string path = root;
            foreach (string part in parts)
            {
                path = Path.Combine(path, Component(part));
                if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic links are not allowed in restore paths");
            }
            return path;
        }

        private static byte[] ReadBounded(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Length == 0 || stream.Length > MaximumBytes) throw new InvalidOperationException("Character file size is invalid");
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException();
                    offset += count;
                }
                return bytes;
            }
        }

        private static T ReadJson<T>(string path)
        {
            using (MemoryStream stream = new MemoryStream(ReadBounded(path)))
                return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(stream)
                    ?? throw new InvalidOperationException("The restore request is empty"));
        }

        private static void WriteNew(string path, byte[] data)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        [DataContract]
        private sealed class Request
        {
            [DataMember] public string account = "";
            [DataMember] public string character = "";
            [DataMember] public string world = "";
            [DataMember] public string before_sha256 = "";
            [DataMember] public string backup_sha256 = "";
            [DataMember] public long expires = 0;
        }

        [DataContract]
        private sealed class Result
        {
            [DataMember] public bool success;
            [DataMember] public string message = "";
            [DataMember] public string sha256 = "";
        }
    }
}
