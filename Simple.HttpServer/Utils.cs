using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Xml;

namespace Simple.HttpServer.Alt
{
    internal class Utils
    {
        /// <summary>
        /// Ensure that specified string has specified suffix.
        /// </summary>
        /// <param name="str">Source string</param>
        /// <param name="ch">Suffix char to check and include if not found</param>
        /// <returns>Source string with suffix</returns>
        public static string IncludeTrailing(string str, char ch)
        {
            return IncludeTrailing(str, "" + ch);
        }

        /// <summary>
        /// Ensure that specified string has specified suffix.
        /// </summary>
        /// <param name="str">Source string</param>
        /// <param name="pattern">Suffix substring to check and include if not found</param>
        /// <returns>Source string with suffix</returns>
        public static string IncludeTrailing(string str, string pattern)
        {
            if (!str.EndsWith(pattern))
                return str + pattern;
            return str;
        }

        /// <summary>Include trailing slash from filepath (both slashes are supported - DirectorySeparatorChar and AltDirectorySeparatorChar)</summary>
        /// <param name="str">Filepath to include trailing slash from</param>
        /// <returns>Filepath with included trailing slash</returns>
        public static string IncludeTrailingSlash(string str)
        {
            int bs = str.IndexOf(Path.DirectorySeparatorChar);
            int fs = str.IndexOf(Path.AltDirectorySeparatorChar);
            if (bs >= 0 || (bs < 0 && fs < 0))
                return IncludeTrailing(str, Path.DirectorySeparatorChar);
            else
                return IncludeTrailing(str, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Ensure that specified string does not have specified suffix.
        /// </summary>
        /// <param name="str">Source string</param>
        /// <param name="ch">Prefix char to check and exclude if found</param>
        /// <returns>Source string without a suffix</returns>
        public static string ExcludeTrailing(string str, char ch)
        {
            return ExcludeTrailing(str, "" + ch);
        }

        /// <summary>
        /// Ensure that specified string does not have specified suffix.
        /// </summary>
        /// <param name="str">Source string</param>
        /// <param name="pattern">Prefix substring to check and exclude if found</param>
        /// <returns>Source string without a suffix</returns>
        public static string ExcludeTrailing(string str, string pattern)
        {
            if (str.EndsWith(pattern))
                return str.Remove(str.Length - pattern.Length, pattern.Length);
            return str;
        }

        public static string ExcludeTrailingSlash(string path)
        {
            int bs = path.IndexOf(Path.DirectorySeparatorChar);
            int fs = path.IndexOf(Path.AltDirectorySeparatorChar);
            return ExcludeTrailing(path, (bs >= 0 || (bs < 0 && fs < 0)) ? Path.DirectorySeparatorChar : Path.AltDirectorySeparatorChar);
        }

        public static int LoadAttributes(Dictionary<string, string> pTargetList, XmlElement pNode, bool pForceLowerCase)
        {
            int count = pTargetList.Count;
            foreach (XmlNode attribute in pNode.Attributes)
            {
                pTargetList[pForceLowerCase ? attribute.Name.ToLower() : attribute.Name] = attribute.Value.TrimEnd();
            }

            return pTargetList.Count - count;
        }

        public static void Merge(Dictionary<string, string> pTargetValues, Dictionary<string, string> pAdditionalValues, bool pForceLowerCaseNames)
        {
            if (pTargetValues == pAdditionalValues)
            {
                return;
            }

            foreach (KeyValuePair<string, string> pAdditionalValue in pAdditionalValues)
            {
                pTargetValues[pForceLowerCaseNames ? pAdditionalValue.Key.ToLower() : pAdditionalValue.Key] = pAdditionalValue.Value;
            }
        }

        //
        // Summary:
        //     Return a string which describing specified exception (exception type name + exception
        //     message)
        public static string FormatErrorMsg(Exception exc)
        {
            return string.Format("ERR({0}): {1}{2}", exc.GetType().ToString(), exc.Message, (exc.InnerException != null) ? " (+internal err!)" : "");
        }

        protected static Assembly _actualAssembly = null;
        /// <summary>Determing which Assembly should be used for getting path, name, version, etc. You can assign your Assembly object to this property</summary>
        public static Assembly ActualAssembly
        {
            get
            {
                if (_actualAssembly == null)
                    _actualAssembly = Assembly.GetEntryAssembly();
                if (_actualAssembly == null)
                    _actualAssembly = Assembly.GetCallingAssembly();
                if (_actualAssembly == null)
                    _actualAssembly = Assembly.GetExecutingAssembly();
                return _actualAssembly;
            }
            set { _actualAssembly = value; }
        }

        public static string ApplicationHomePath
        {
            get
            {
                Assembly asm = ActualAssembly;
                if (asm != null)
                {
                    return IncludeTrailingSlash(Path.GetDirectoryName(asm.Location));
                }
                return ConfigurationManager.AppSettings["AppHomePath"];
            }
        }

    }
}
