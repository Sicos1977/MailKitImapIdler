using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;

//
// Connection.cs
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
    #region ClientType
    /// <summary>
    ///     The type of client we are going to use to connect to the mailserver
    /// </summary>
    internal enum ClientType
    {
        /// <summary>
        ///     The connection is an <see cref="ImapClient" /> connection
        /// </summary>
        Imap,

        /// <summary>
        ///     The connection is an <see cref="Pop3Client" /> connection
        /// </summary>
        Pop3
    }
    #endregion

    /// <summary>
    ///     Used to keep track of the connection we are monitoring
    /// </summary>
    internal class Connection
    {
        #region Properties
        /// <summary>
        ///     Returns the user name for the mail server
        /// </summary>
        public string UserName { get; internal set; }

        /// <summary>
        ///     Returns the password for the <see cref="UserName" />
        /// </summary>
        public string Password { get; internal set; }

        /// <summary>
        ///     Returns the host name for the mail server
        /// </summary>
        public string Host { get; internal set; }

        /// <summary>
        ///     Returns the port for the mail server
        /// </summary>
        public int Port { get; internal set; }

        /// <summary>
        ///     Returns the mail server folder to open
        /// </summary>
        /// <remarks>
        ///     Not used when the <see cref="Client"/> is an <see cref="ImapClient"/>
        /// </remarks>
        public string FolderName { get; internal set; }

        /// <summary>
        ///     Returns the <see cref="SearchQuery" /> to use when reading (selecting) e-mail
        /// </summary>
        /// <remarks>
        ///     Not used when the <see cref="Client"/> is an <see cref="ImapClient"/>
        /// </remarks>
        public SearchQuery SearchQuery { get; internal set; }

        /// <summary>
        ///     Returns the <see cref="SecureSocketOptions" /> to use when connecting the mail server
        /// </summary>
        public SecureSocketOptions Options { get; internal set; }

        /// <summary>
        ///     Returns the directory where the received e-mails will be written
        /// </summary>
        public DirectoryInfo OutputDirectory { get; internal set; }

        /// <summary>
        ///     The interval in seconds to use when the connection is in <see cref="MailKit.Net.Imap.ImapClient.IdleAsync" />
        ///     or <see cref="MailKit.Net.Imap.ImapClient.NoOpAsync" /> mode before resending these commands to the mail server
        /// </summary>
        public int IdleOrNopInterval { get; internal set; }

        /// <summary>
        ///     The interval in seconds to use when checking for the arival of new e-mails.
        /// </summary>
        /// <remarks>
        ///     This property is only used when the mail server does not support
        ///     <see cref="MailKit.Net.Imap.ImapClient.IdleAsync" /> mode
        /// </remarks>
        public int MailPollingInterval { get; internal set; }

        /// <summary>
        ///     Returns the actions that need to be done after an messages has been retrieved from a <see cref="Connection" />
        /// </summary>
        /// <remarks>
        ///     Not used when the <see cref="Client"/> is an <see cref="ImapClient"/>
        /// </remarks>
        public PostProcessing PostProcessing { get; internal set; }

        /// <summary>
        ///     Returns the mail server folder where processed messages are moved to when the <see cref="PostProcessing" />
        ///     is set to <see cref="MailKitImapIdler.PostProcessing.MoveToFolder" /> or
        ///     <see cref="MailKitImapIdler.PostProcessing.FlagAsSeenAndMoveToFolder" />.
        /// </summary>
        /// <remarks>
        ///     Not used when the <see cref="Client"/> is an <see cref="ImapClient"/>
        /// </remarks>
        public string DestinationFolderName { get; internal set; }

        /// <summary>
        ///     <see cref="ClientType" />
        /// </summary>
        internal ClientType Type { get; set; }

        /// <summary>
        ///     Returns the <see cref="ImapClient" /> or <see cref="Pop3Client" />
        /// </summary>
        internal object Client { get; set; }

        /// <summary>
        ///     Returns the <see cref="ImapFolder" /> that is opened
        /// </summary>
        /// <remarks>
        ///     Not used when the <see cref="Client"/> is an <see cref="ImapClient"/>
        /// </remarks>
        internal IMailFolder Folder { get; set; }

        /// <summary>
        ///     Returns true when the <see cref="Client" /> is an <see cref="ImapClient" /> and
        ///     when it's supports <see cref="ImapClient.Idle" /> mode
        /// </summary>
        internal bool CanIdle { get; set; }

        /// <summary>
        ///     Returns the date and time when we called the <see cref="ImapClient.IdleAsync" /> or
        ///     <see cref="ImapClient.NoOpAsync" /> method for the last time
        /// </summary>
        internal DateTime IdlingOrNopTimeStamp { get; set; }

        /// <summary>
        ///     The date and time when we checked the <see cref="Client" /> for newly arived message
        ///     for the last time
        /// </summary>
        /// <remarks>
        ///     This property is only used when the mail server does not support <see cref="ImapClient.IdleAsync" /> mode
        /// </remarks>
        internal DateTime MailPollingTimeStamp { get; set; }

        /// <summary>
        ///     Used to cancel the <see cref="IdleOrNoOpTask" />
        /// </summary>
        internal CancellationTokenSource IdleOrNoOpTaskDone { get; set; }

        /// <summary>
        ///     Used to keep track of the idling or noop task
        /// </summary>
        internal Task IdleOrNoOpTask { get; set; }

        /// <summary>
        ///     Used to keep track of the download task
        /// </summary>
        internal Task DownloadTask { get; set; }

        /// <summary>
        ///     Used to cancel the <see cref="DownloadTask" />
        /// </summary>
        internal CancellationTokenSource DownloadTaskDone { get; set; }

        /// <summary>
        ///     Indicates that the connection is busy processing e-mails
        /// </summary>
        internal bool IsBusy { get; set; }

        /// <summary>
        ///     Used to keep track of when we last did write an error to the logging.
        ///     This is to prevent that we blow up the log files when we get many repetive errors
        /// </summary>
        internal DateTime LastErrorTimeStamp { get; set; }
        #endregion

        #region Constructors
        /// <summary>
        ///     Makes this object and sets all the properties that are needed for an <see cref="ImapClient" /> connection
        /// </summary>
        /// <param name="userName">The mail server user name</param>
        /// <param name="password">The password for the <paramref name="userName" /></param>
        /// <param name="host">The host name from the mail server</param>
        /// <param name="port">The port from the mail server</param>
        /// <param name="options">The <see cref="SecureSocketOptions" /> to use when connecting the mail server</param>
        /// <param name="folderName">The mail server folder to use (standard this should be INBOX)</param>
        /// <param name="searchQuery">The <see cref="SearchQuery" /> to use when reading (selecting) e-mail</param>
        /// <param name="outputDirectory">The path where the recieved e-mails will be written</param>
        /// <param name="idleOrNoOpInterval">
        ///     The interval in seconds to use when the connection is in <see cref="MailKit.Net.Imap.ImapClient.IdleAsync" />
        ///     or <see cref="MailKit.Net.Imap.ImapClient.NoOpAsync" /> mode before resending these commands to the mail server
        /// </param>
        /// <param name="mailPollingInterval">
        ///     The interval in seconds to use when checking for the arival of new e-mails.
        ///     This parameter is only used when the mail server does not support
        ///     <see cref="MailKit.Net.Imap.ImapClient.IdleAsync" /> mode
        /// </param>
        /// <param name="postProcessing">
        ///     The actions that need to be done after an messages has been retrieved from
        ///     a <see cref="Connection" />
        /// </param>
        /// <param name="destinationFolderName">
        ///     The mail server folder where processed messages are moved to when the <paramref name="postProcessing" />
        ///     is set to <see cref="MailKitImapIdler.PostProcessing.MoveToFolder" /> or
        ///     <see cref="MailKitImapIdler.PostProcessing.FlagAsSeenAndMoveToFolder" />.
        ///     This parameter will be ignore if any other value is set
        /// </param>
        internal Connection(string userName,
            string password,
            string host,
            int port,
            SecureSocketOptions options,
            string folderName,
            SearchQuery searchQuery,
            string outputDirectory,
            int idleOrNoOpInterval,
            int mailPollingInterval,
            PostProcessing postProcessing,
            string destinationFolderName)
        {
            Type = ClientType.Imap;
            UserName = userName;
            Password = password;
            Host = host;
            Port = port;
            Options = options;
            FolderName = folderName;
            SearchQuery = searchQuery;
            OutputDirectory = Directory.CreateDirectory(outputDirectory);
            IdleOrNopInterval = idleOrNoOpInterval;
            MailPollingInterval = mailPollingInterval;
            PostProcessing = postProcessing;
            DestinationFolderName = destinationFolderName;
        }

        /// <summary>
        ///     Makes this object and sets all the properties that are needed for an <see cref="Pop3Client" /> connection
        /// </summary>
        /// <param name="userName">The mail server user name</param>
        /// <param name="password">The password for the <paramref name="userName" /></param>
        /// <param name="host">The host name from the mail server</param>
        /// <param name="port">The port from the mail server</param>
        /// <param name="options">The <see cref="SecureSocketOptions" /> to use when connecting the mail server</param>
        /// <param name="outputDirectory">The path where the recieved e-mails will be written</param>
        /// <param name="noOpInterval">
        ///     The interval in seconds to use when the connection is in <see cref="Pop3Client.NoOpAsync" />
        ///     or <see cref="Pop3Client.NoOpAsync" /> mode before resending these commands to the mail server
        /// </param>
        /// <param name="mailPollingInterval">The interval in seconds to use when checking for the arival of new e-mails</param>
        internal Connection(string userName,
            string password,
            string host,
            int port,
            SecureSocketOptions options,
            string outputDirectory,
            int noOpInterval,
            int mailPollingInterval)
        {
            Type = ClientType.Pop3;
            UserName = userName;
            Password = password;
            Host = host;
            Port = port;
            Options = options;
            OutputDirectory = Directory.CreateDirectory(outputDirectory);
            IdleOrNopInterval = noOpInterval;
            MailPollingInterval = mailPollingInterval;
        }
        #endregion
    }
}