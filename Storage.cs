using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace PowerTimer {
    public static class Store {
        public static T Read<T>(string path) where T : class {
            try {
                if (new FileInfo(path).Length > 65536) return null;
                return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path));
            } catch (IOException) { return null; }
              catch (UnauthorizedAccessException) { return null; }
              catch (ArgumentException) { return null; }
              catch (InvalidOperationException) { return null; }
        }
        public static void Write(string path, object value) {
            WriteText(path, new JavaScriptSerializer().Serialize(value));
        }
        public static void WriteChanged(string path, object value, ref string previous) {
            string json = new JavaScriptSerializer().Serialize(value);
            if (json == previous) return;
            WriteText(path, json);
            previous = json;
        }
        static void WriteText(string path, string text) {
            string temp = path + ".tmp";
            try {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            } finally {
                // Failure keeps the previous complete file and removes the partial one.
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
                  catch (UnauthorizedAccessException) { }
            }
        }
        public static void SecureDirectory(string path) {
            Directory.CreateDirectory(path);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, acl);
        }
    }
}
