using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Linq;

namespace Simple.FtpServer
{
    internal static class FtpStorageSecurity
    {
        public static void SecureDirectory(string path, IEnumerable<SecurityIdentifier> authorizedOperators)
        {
            EnsureNoReparsePoints(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var identity in GetIdentities(authorizedOperators))
            {
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(path).SetAccessControl(security);
        }

        public static void SecureFile(string path, IEnumerable<SecurityIdentifier> authorizedOperators)
        {
            EnsureNoReparsePoints(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var identity in GetIdentities(authorizedOperators))
            {
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            new FileInfo(path).SetAccessControl(security);
        }

        public static void EnsureNoReparsePoints(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            var current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Storage paths cannot traverse reparse points.");
            }

            var segments = fullPath.Substring(root.Length).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    break;
                }

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Storage paths cannot traverse reparse points.");
                }
            }
        }

        private static IEnumerable<SecurityIdentifier> GetIdentities(IEnumerable<SecurityIdentifier> operators)
        {
            var identities = new HashSet<SecurityIdentifier>();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null)
            {
                throw new InvalidOperationException("The current Windows identity has no security identifier.");
            }

            identities.Add(currentUser);
            identities.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            identities.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            if (operators != null)
            {
                foreach (var identity in operators)
                {
                    if (identity == null)
                    {
                        throw new ArgumentException("Authorized operator SIDs cannot contain null entries.", nameof(operators));
                    }

                    identities.Add(identity);
                }
            }

            return identities.ToArray();
        }
    }
}