/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.IO;
using UnityEngine;

namespace Antigravity.Ide.Editor
{
	internal static class FileUtility
	{
		public const char WinSeparator = '\\';
		public const char UnixSeparator = '/';

		public static string GetAbsolutePath(string path)
		{
#if UNITY_6000_5_OR_NEWER
			return UnityEditor.FileUtil
				.PathToAbsolutePath(path)
				.NormalizePathSeparators();
#else
			return Path.GetFullPath(path);
#endif
		}

		public static string GetPackageAssetFullPath(params string[] components)
		{
			// Unity has special IO handling of Packages and will resolve those path to the right package location
			// Adapted for Antigravity package name though this method might not be used directly or might need adjustment
			// Keeping as com.antigravity.ide
			return GetAbsolutePath(Path.Combine("Packages", "com.antigravity.ide", Path.Combine(components)));
		}

		public static string GetAssetFullPath(string asset)
		{
			var basePath = GetAbsolutePath(Path.Combine(Application.dataPath, ".."));
			return GetAbsolutePath(Path.Combine(basePath, NormalizePathSeparators(asset)));
		}

		public static string NormalizePathSeparators(this string path)
		{
			if (string.IsNullOrEmpty(path))
				return path;

			if (Path.DirectorySeparatorChar == WinSeparator)
				path = path.Replace(UnixSeparator, WinSeparator);
			if (Path.DirectorySeparatorChar == UnixSeparator)
				path = path.Replace(WinSeparator, UnixSeparator);

			return path.Replace(string.Concat(WinSeparator, WinSeparator), WinSeparator.ToString());
		}

		public static string NormalizeWindowsToUnix(this string path)
		{
			if (string.IsNullOrEmpty(path))
				return path;

			return path.Replace(WinSeparator, UnixSeparator);
		}

		internal static bool IsFileInProjectRootDirectory(string fileName)
		{
			var relative = MakeRelativeToProjectPath(fileName);
			if (string.IsNullOrEmpty(relative))
				return false;

			return relative == Path.GetFileName(relative);
		}

		public static string MakeAbsolutePath(this string path)
		{
			if (string.IsNullOrEmpty(path)) { return string.Empty; }
			return Path.IsPathRooted(path) ? path : GetAbsolutePath(path);
		}
		
		// returns null if outside of the project scope
		internal static string MakeRelativeToProjectPath(string fileName)
		{
			var basePath = GetAbsolutePath(Path.Combine(Application.dataPath, ".."));
			fileName = NormalizePathSeparators(fileName);

			if (!Path.IsPathRooted(fileName))
				fileName = Path.Combine(basePath, fileName);

			if (!fileName.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
				return null;

			return fileName
				.Substring(basePath.Length)
				.Trim(Path.DirectorySeparatorChar);
		}
	}
}
