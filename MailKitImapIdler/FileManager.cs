﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

//
// FileManager.cs
//
// Author: Kees van Spelde
//
// Copyright (c) 2016 Kees van Spelde
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

namespace MailKitImapIdler
{
    #region Class FileManagerFileSizeInfo
    /// <summary>
    /// Deze classe wordt gebruikt om informatie te retourneren vanuit de <see cref="FileManager.GetFileSizeInfo"/> methode
    /// </summary>
    public class FileManagerFileSizeInfo
    {
        /// <summary>
        /// De size na herberekening afgerond op 2 getallen na de comma
        /// </summary>
        // ReSharper disable once MemberCanBePrivate.Global
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public double Size { get; private set; }

        /// <summary>
        /// Het getal waardoor is gedeeld
        /// </summary>
        public double Divider { get; private set; }

        /// <summary>
        /// Het formaat: GB, MB, KB of Bytes
        /// </summary>
        public string Format { get; private set; }

        internal FileManagerFileSizeInfo(double size, double divider, string format)
        {
            Size = size;
            Divider = divider;
            Format = format;
        }
    }
    #endregion

    /// <summary>
    /// This class contains file and folder related helper methods
    /// </summary>
    public static class FileManager
    {
        #region Consts
        /// <summary>
        /// De maximale pad lengte in Windows
        /// </summary>
        private const int MaxPath = 248;
        #endregion

        #region CheckForBackSlash
        /// <summary>
        /// Deze functie controleert of er aan het einde van de string een backslash staat en zo
        /// nee dan wordt deze er achter geplaatst
        /// </summary>
        /// <param name="line"></param>
        /// <returns>De regel met daarachter een slash "\"</returns>
        public static string CheckForBackSlash(string line)
        {
            if (string.IsNullOrEmpty(line))
                throw new ArgumentNullException(line);

            if (line.EndsWith("\\", StringComparison.Ordinal))
                return line;

            return line + "\\";
        }
        #endregion

        #region GetFileSizeString
        /// <summary>
        /// Geeft de size van een bestand in Windows formaat (GB, MB, KB, Bytes)
        /// </summary>
        /// <param name="bytes">Aantal bytes</param>
        /// <returns></returns>
        public static string GetFileSizeString(double bytes)
        {
            var size = "0 Bytes";
            if (bytes >= 1073741824.0)
                size = String.Format(CultureInfo.InvariantCulture, "{0:##.##}", bytes / 1073741824.0) + " GB";
            else if (bytes >= 1048576.0)
                size = String.Format(CultureInfo.InvariantCulture, "{0:##.##}", bytes / 1048576.0) + " MB";
            else if (bytes >= 1024.0)
                size = String.Format(CultureInfo.InvariantCulture, "{0:##.##}", bytes / 1024.0) + " KB";
            else if (bytes > 0 && bytes < 1024.0)
                size = bytes + " Bytes";

            return size;
        }

        /// <summary>
        /// Geeft de size van een bestand in Windows formaat (GB, MB, KB, Bytes)
        /// </summary>
        /// <param name="bytes">De size in bytes</param>
        /// <returns></returns>
        public static FileManagerFileSizeInfo GetFileSizeInfo(double bytes)
        {
            var size = 0.0;
            var divider = 0.0;
            var format = "Bytes";

            if (bytes >= 1073741824.0)
            {
                divider = 1073741824.0;
                size = bytes / divider;
                format = "GB";
            }
            else if (bytes >= 1048576.0)
            {
                divider = 1048576.0;
                size = bytes / divider;
                format = "MB";
            }
            else if (bytes >= 1024.0)
            {
                divider = 1024.0;
                size = bytes / divider;
                format = "KB";
            }
            else if (bytes > 0 && bytes < 1024.0)
            {
                divider = 0.0;
                size = bytes;
                format = "Bytes";
            }

            return new FileManagerFileSizeInfo(size, divider, format);
        }
        #endregion

        #region FileExistsMakeNew
        /// <summary>
        /// Checks if a file already exists and if so adds a number until the file is unique
        /// </summary>
        /// <param name="fileName">The file to check</param>
        /// <param name="validateLongFileName">When true validation will be performed on the max path lengt</param>
        /// <param name="extraTruncateSize"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">Raised when no path or file name is given in the <paramref name="fileName"/></exception>
        /// <exception cref="PathTooLongException">Raised when it is not possible to truncate the <paramref name="fileName"/></exception>
        public static string FileExistsMakeNew(string fileName, bool validateLongFileName = true, int extraTruncateSize = -1)
        {
            var fileNameWithoutExtension = GetFileNameWithoutExtension(fileName);
            var extension = GetExtension(fileName);
            var path = CheckForBackSlash(GetDirectoryName(fileName));

            var tempFileName = validateLongFileName ? ValidateLongFileName(fileName, extraTruncateSize) : fileName;

            var i = 2;
            while (File.Exists(tempFileName))
            {
                tempFileName = path + fileNameWithoutExtension + "_" + i + extension;
                tempFileName = validateLongFileName ? ValidateLongFileName(tempFileName, extraTruncateSize) : tempFileName;
                i += 1;
            }

            return tempFileName;
        }
        #endregion

        #region RemoveInvalidCharsFromFileName
        /// <summary>
        /// Removes illegal filename characters
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public static string RemoveInvalidFileNameChars(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return fileName;

            var result = Path.GetInvalidFileNameChars()
                .Aggregate(fileName, (current, c) => current.Replace(c.ToString(CultureInfo.InvariantCulture), "_"));

            return result;
        }
        #endregion

        #region ValidateLongFileName
        /// <summary>
        /// Validates the length of <paramref name="fileName"/>, when this is longer then
        /// 260 chars it will be truncated.
        /// zodat deze wel past
        /// </summary>
        /// <param name="fileName">The filename with path</param>
        /// <param name="extraTruncateSize">Optional extra truncate size, when not used the filename is truncated until it fits</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">Raised when no path or file name is given in the <paramref name="fileName"/></exception>
        /// <exception cref="PathTooLongException">Raised when it is not possible to truncate the <paramref name="fileName"/></exception>
        public static string ValidateLongFileName(string fileName, int extraTruncateSize = -1)
        {
            var fileNameWithoutExtension = GetFileNameWithoutExtension(fileName);

            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                throw new ArgumentException(@"No file name is given, e.g. c:\temp\temp.txt", "fileName");

            var extension = GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(extension))
                extension = string.Empty;

            var path = GetDirectoryName(fileName);

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(@"No path is given, e.g. c:\temp\temp.txt", "fileName");

            path = CheckForBackSlash(path);

            if (fileName.Length <= MaxPath)
                return fileName;

            var maxFileNameLength = MaxPath - path.Length - extension.Length;
            if (extraTruncateSize != -1)
                maxFileNameLength -= extraTruncateSize;

            if (maxFileNameLength < 1)
                throw new PathTooLongException("Unable the truncate the fileName '" + fileName + "', current size '" +
                                               fileName.Length + "'");

            return path + fileNameWithoutExtension.Substring(0, maxFileNameLength) + extension;
        }
        #endregion

        #region GetExtension
        /// <summary>
        /// Returns the extension of the specified <paramref name="path"/> string
        /// </summary>
        /// <param name="path">The path of the file</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">Raised when no path is given</exception>
        public static string GetExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path");

            var splittedPath = path.Split(Path.DirectorySeparatorChar);
            var fileName = splittedPath[splittedPath.Length - 1];

            var index = fileName.LastIndexOf(".", StringComparison.Ordinal);

            return index == -1
                ? string.Empty
                : fileName.Substring(fileName.LastIndexOf(".", StringComparison.Ordinal), fileName.Length - index);
        }
        #endregion

        #region GetFileNameWithoutExtension
        /// <summary>
        /// Returns the file name of the specified <paramref name="path"/> string without the extension
        /// </summary>
        /// <param name="path">The path of the file</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static string GetFileNameWithoutExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(@"No path given", "path");

            var splittedPath = path.Split(Path.DirectorySeparatorChar);
            var fileName = splittedPath[splittedPath.Length - 1];
            return !fileName.Contains(".")
                ? fileName
                : fileName.Substring(0, fileName.LastIndexOf(".", StringComparison.Ordinal));
        }
        #endregion

        #region GetDirectoryName
        /// <summary>
        /// Returns the directory information for the specified <paramref name="path"/> string
        /// </summary>
        /// <param name="path">The path of a file or directory</param>
        /// <returns></returns>
        public static string GetDirectoryName(string path)
        {
            //GetDirectoryName('C:\MyDir\MySubDir\myfile.ext') returns 'C:\MyDir\MySubDir'
            //GetDirectoryName('C:\MyDir\MySubDir') returns 'C:\MyDir'
            //GetDirectoryName('C:\MyDir\') returns 'C:\MyDir'
            //GetDirectoryName('C:\MyDir') returns 'C:\'
            //GetDirectoryName('C:\') returns ''

            var splittedPath = path.Split(Path.DirectorySeparatorChar);

            if (splittedPath.Length <= 1)
                return string.Empty;

            var result = splittedPath[0];

            for (var i = 1; i < splittedPath.Length - 1; i++)
                result += Path.DirectorySeparatorChar + splittedPath[i];

            return result;
        }
        #endregion

        #region IsValidPath
        /// <summary>
        /// Controleert of het opgegeven pad valide is. Er wordt niet gecontroleerd of het pad ook
        /// echt bestaat
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static bool IsValidPath(string path)
        {
            var regex = new Regex(@"^(([a-zA-Z]\:)|(\\))(\\{1}|((\\{1})[^\\]([^/:*?<>""|]*))+)$");
            var result = regex.IsMatch(path);

            if (result)
            {
                try
                {
                    // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                    Path.GetFullPath(path);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return result;
        }
        #endregion

        #region PathCombine
        /// <summary>
        /// Combines the 2 path strings
        /// </summary>
        /// <param name="path1"></param>
        /// <param name="path2"></param>
        /// <returns></returns>
        public static string PathCombine(string path1, string path2)
        {
            path1 = path1.Trim('\\');
            path2 = path2.Trim('\\');

            return path1 + "\\" + path2;
        }
        #endregion
    }
}