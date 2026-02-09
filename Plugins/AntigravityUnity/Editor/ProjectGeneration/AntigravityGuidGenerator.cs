using System;
using System.Security.Cryptography;
using System.Text;

namespace Antigravity.Ide.Editor
{
    internal static class AntigravityGuidGenerator
    {
        public static string GuidForProject(string projectName)
        {
            return ComputeGuidHash(projectName);
        }

        public static string GuidForSolution(string projectName, string extension)
        {
            return ComputeGuidHash(projectName + extension);
        }

        private static string ComputeGuidHash(string input)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                return new Guid(hash).ToString("D").ToUpper();
            }
        }
    }
}
