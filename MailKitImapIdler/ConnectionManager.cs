using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;

// ReSharper disable InconsistentlySynchronizedField

//
// ConnectionManager.cs
//
// Author: Kees van Spelde
//
// Copyright (c) 2016 - 2018 Kees van Spelde
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
    #region PostProcessing
    /// <summary>
    ///     The actions that need to be done after an messages has been retrieved from
    ///     a <see cref="Connection" />
    /// </summary>
    public enum PostProcessing
    {
        /// <summary>
        ///     Delete the message from the folder
        /// </summary>
        /// <remarks>
        ///     The message will always first be flagged as <see cref="MessageFlags.Seen" />
        ///     before it is deleted.
        /// </remarks>
        DeleteFromFolder,

        /// <summary>
        ///     Move the message to another folder
        /// </summary>
        MoveToFolder,

        /// <summary>
        ///     Set the <see cref="MessageFlags.Seen" /> flag and
        ///     then move the message to another folder
        /// </summary>
        FlagAsSeenAndMoveToFolder,

        /// <summary>
        ///     Set the <see cref="MessageFlags.Seen" /> flag
        /// </summary>
        FlagAsSeen
    }
    #endregion

    internal class ConnectionManager : IDisposable
    {
        #region Fields
        /// <summary>
        ///     Used to keep track if the <see cref="Start" /> method already has been called
        /// </summary>
        private volatile bool _running;

        /// <summary>
        ///     Used to keep track of all the connections that we are monitoring
        /// </summary>
        private readonly List<Connection> _connections = new List<Connection>();

        /// <summary>
        ///     Contains the <see cref="Connection" /> objects that did recieve new e-mails and
        ///     need to be processed by the <see cref="MessageProcessingTaskManager" />
        /// </summary>
        private ConcurrentQueue<Connection> _connectionProcessingQueue;

        /// <summary>
        ///     Used to signal the <see cref="MessageProcessingTaskManager" /> method from the <see cref="ConnectionWorker" />
        ///     method that a new e-mail has arrived for a <see cref="Connection" />
        /// </summary>
        private readonly AutoResetEvent _newMailTrigger = new AutoResetEvent(false);

        /// <summary>
        ///     Used to keep track if the connections already have been disposed
        /// </summary>
        private bool _disposed;

        /// <summary>
        ///     Used to keep track of all the worker tasks we are starting
        /// </summary>
        private List<Task> _workerTasks;

        /// <summary>
        ///     Used to set the maximum on parallel downloading e-mails, -1 is no limit
        /// </summary>
        private readonly int _maxDownloads;

        /// <summary>
        ///     When set then logging is written to this stream
        /// </summary>
        private static Stream _logStream;

        /// <summary>
        ///     When set then very detailed logging will be written
        /// </summary>
        /// <remarks>
        ///     Only use this when needed because this will slowdown the processing of e-mails
        /// </remarks>
        private readonly bool _debugMode;

        /// <summary>
        ///     <see cref="LimitedConcurrencyLevelTaskScheduler" />
        /// </summary>
        private readonly TaskFactory _taskFactory;
        #endregion

        #region Constructor
        /// <summary>
        ///     Makes this object and sets all it's properties
        /// </summary>
        /// <param name="logStream">When set then logging is written to this stream</param>
        /// <param name="maxDownloads">
        ///     The maximum parallel downloads from messages from different <see cref="Connection" />'s,
        ///     -1 means that there is no limit
        /// </param>
        /// <param name="debugMode"></param>
        internal ConnectionManager(Stream logStream = null,
            int maxDownloads = -1,
            bool debugMode = false)
        {
            ServicePointManager.DefaultConnectionLimit = int.MaxValue;
            _logStream = logStream;
            if (maxDownloads == 0)
                throw new ArgumentException("The value must be -1 or any number greater then 0", "maxDownloads");
            _maxDownloads = maxDownloads;
            _debugMode = debugMode;
            var lcts = new LimitedConcurrencyLevelTaskScheduler(100);
            _taskFactory = new TaskFactory(lcts);
        }
        #endregion

        #region AddImapConnection
        /// <summary>
        ///     Add's a new IMAP connection
        /// </summary>
        /// <param name="userName">The mail server username</param>
        /// <param name="password">The password for the <paramref name="userName" /></param>
        /// <param name="host">The mail server hostname</param>
        /// <param name="port">The mail server port</param>
        /// <param name="options">The <see cref="SecureSocketOptions" /> to use when connecting the mail server</param>
        /// <param name="folderName">the mail server folder to use, standard INBOX</param>
        /// <param name="searchQuery">The <see cref="SearchQuery" /> to use when reading (selecting) e-mail</param>
        /// <param name="outputDirectory">The path where the recieved e-mails will be written</param>
        /// <param name="idleOrNoOpInterval">
        ///     The interval in seconds to use when the connection is in <see cref="ImapClient.IdleAsync" />
        ///     or <see cref="ImapClient.NoOpAsync" /> mode before resending these commands to the mail server
        /// </param>
        /// <param name="mailPollingInterval">
        ///     The interval in seconds to use when checking for the arrival of new e-mails.
        ///     This parameter is only used when the mail server does not support <see cref="ImapClient.IdleAsync" /> mode
        /// </param>
        /// <param name="postProcessing">
        ///     The actions that need to be done after an messages has been retrieved from a <see cref="Connection" />
        /// </param>
        /// <param name="destinationFolderName">
        ///     The mail server folder where processed messages are moved to when the <paramref name="postProcessing" />
        ///     is set to <see cref="PostProcessing.MoveToFolder" /> or <see cref="PostProcessing.FlagAsSeenAndMoveToFolder" />.
        ///     This parameter will be ignore if any other value is set
        /// </param>
        public void AddImapConnection(
            string userName,
            string password,
            string host,
            int port,
            SecureSocketOptions options,
            string folderName,
            SearchQuery searchQuery,
            string outputDirectory,
            int idleOrNoOpInterval = 29 * 60,
            int mailPollingInterval = 60,
            PostProcessing postProcessing = PostProcessing.DeleteFromFolder,
            string destinationFolderName = null)
        {
            if (_running)
                throw new ArgumentException("Can't add new connection when already started");

            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentNullException("userName", "Cannot be null or an empty or whitespace string");

            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentNullException("host", "Cannot be null or an empty or whitespace string");

            if (string.IsNullOrWhiteSpace(folderName))
                throw new ArgumentNullException("folderName", "Cannot be null or an empty or whitespace string");

            if (port < 0 || port > 65535)
                throw new ArgumentOutOfRangeException("port", "The port needs to be in the range 0 - 65535");

            if (idleOrNoOpInterval < 1)
                throw new ArgumentOutOfRangeException("idleOrNoOpInterval", "Needs to be 1 or greater");

            if (mailPollingInterval < 1)
                throw new ArgumentOutOfRangeException("mailPollingInterval", "Needs to be 1 or greater");

            switch (postProcessing)
            {
                case PostProcessing.MoveToFolder:
                case PostProcessing.FlagAsSeenAndMoveToFolder:
                    if (string.IsNullOrWhiteSpace(destinationFolderName))
                        throw new ArgumentException(
                            "The parameter has to be set when the postprocessing parameter is set to MoveToFolder or FlagAsSeenAndMoveToFolder",
                            "destinationFolderName");
                    break;
            }

            // Only add the connection when in not already exists
            if (_connections.FirstOrDefault(
                m =>
                    string.Equals(m.Host, host, StringComparison.InvariantCultureIgnoreCase) &&
                    string.Equals(m.UserName, userName, StringComparison.InvariantCultureIgnoreCase) &&
                    string.Equals(m.FolderName, folderName, StringComparison.InvariantCultureIgnoreCase)) != null)
                return;

            _connections.Add(new Connection(userName, password, host, port, options, folderName, searchQuery,
                outputDirectory,
                idleOrNoOpInterval, mailPollingInterval, postProcessing, destinationFolderName));
        }
        #endregion

        #region AddPop3Connection
        /// <summary>
        ///     Add's a new POP3 connection
        /// </summary>
        /// <param name="userName">The mail server username</param>
        /// <param name="password">The password for the <paramref name="userName" /></param>
        /// <param name="host">The mail server hostname</param>
        /// <param name="port">The mail server port</param>
        /// <param name="options">The <see cref="SecureSocketOptions" /> to use when connecting the mail server</param>
        /// <param name="outputDirectory">The path where the recieved e-mails will be written</param>
        /// <param name="noOpInterval">
        ///     The interval in seconds to use when the connection is in <see cref="Pop3Client.NoOpAsync" />
        ///     or <see cref="Pop3Client.NoOpAsync" /> mode before resending these commands to the mail server
        /// </param>
        /// <param name="mailPollingInterval">
        ///     The interval in seconds to use when checking for the arrival of new e-mails.
        ///     This parameter is only used when the mail server does not support <see cref="ImapClient.IdleAsync" /> mode
        /// </param>
        public void AddPop3Connection(
            string userName,
            string password,
            string host,
            int port,
            SecureSocketOptions options,
            string outputDirectory,
            int noOpInterval = 29 * 60,
            int mailPollingInterval = 60)
        {
            if (_running)
                throw new ArgumentException("Can't add new connection when already started");

            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentNullException("userName", "Cannot be null or an empty or whitespace string");

            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentNullException("host", "Cannot be null or an empty or whitespace string");

            if (port < 0 || port > 65535)
                throw new ArgumentOutOfRangeException("port", "The port needs to be in the range 0 - 65535");

            if (noOpInterval < 1)
                throw new ArgumentOutOfRangeException("noOpInterval", "Needs to be 1 or greater");

            if (mailPollingInterval < 1)
                throw new ArgumentOutOfRangeException("mailPollingInterval", "Needs to be 1 or greater");

            // Only add the connection when in not already exists
            if (_connections.FirstOrDefault(
                m =>
                    string.Equals(m.Host, host, StringComparison.InvariantCultureIgnoreCase) &&
                    string.Equals(m.UserName, userName, StringComparison.InvariantCultureIgnoreCase)) != null)
                return;

            _connections.Add(new Connection(userName, password, host, port, options,
                outputDirectory, noOpInterval, mailPollingInterval));
        }
        #endregion

        #region Start
        /// <summary>
        ///     This method will start all the tasks that are needed to process e-mails from the added <see cref="_connections" />
        /// </summary>
        public void Start()
        {
            if (_running)
            {
                WriteLineToLogStream("Already started");
                return;
            }

            _running = true;
            _disposed = false;
            _connectionProcessingQueue = new ConcurrentQueue<Connection>();
            _workerTasks = new List<Task>
            {
                Task.Factory.StartNew(ConnectionWorker, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(MessageProcessingTaskManager, TaskCreationOptions.LongRunning)
            };
        }
        #endregion

        #region ConnectionWorker
        /// <summary>
        ///     This method will startup and maintain all the added <see cref="_connections" />
        /// </summary>
        private void ConnectionWorker()
        {
            WriteLineToLogStream("ConnectionWorker started");

            while (_running)
            {
                foreach (var connection in _connections)
                {
                    // Indicates that the connection is busy downloading new e-mails
                    if (connection.IsBusy) continue;

                    try
                    {
                        // Set's the Imap or Pop3 client
                        if (connection.Client == null)
                        {
                            switch (connection.Type)
                            {
                                case ClientType.Imap:
                                    WriteLineToLogStream("*** IMAP client ***");

                                    connection.Client = _debugMode && _logStream != null
                                        ? new ImapClient(new ProtocolLogger(_logStream))
                                        : new ImapClient();

                                    break;

                                case ClientType.Pop3:
                                    WriteLineToLogStream("** POP3 client");

                                    connection.Client = _debugMode && _logStream != null
                                        ? new Pop3Client(new ProtocolLogger(_logStream))
                                        : new Pop3Client();

                                    break;

                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }

                        var client = (MailService)connection.Client;

                        // Check if we are connected
                        if (!client.IsConnected)
                        {
                            WriteLineToLogStream("Connecting to host '{0}' on port {1}", connection.Host,
                                connection.Port);
                            client.Connect(connection.Host, connection.Port, connection.Options);
                            WriteLineToLogStream("... connected");

                            if (connection.Type == ClientType.Imap)
                            {
                                var imapClient = (ImapClient)connection.Client;
                                connection.CanIdle = imapClient.Capabilities.HasFlag(ImapCapabilities.Idle);
                                if (imapClient.Capabilities.HasFlag(ImapCapabilities.Compress))
                                {
                                    imapClient.Compress();
                                    WriteLineToLogStream("Compression enabled to reduce band width");
                                }
                                break;
                            }
                        }

                        // Authenticate the client
                        if (!client.IsAuthenticated)
                        {
                            WriteLineToLogStream("Authenticating user '{0}'", connection.UserName);
                            client.Authenticate(connection.UserName, connection.Password);
                            WriteLineToLogStream("... authenticated");
                        }

                        // Only Imap supports folders
                        if (connection.Type == ClientType.Imap)
                        {
                            var imapClient = (ImapClient)connection.Client;
                            var folder = imapClient.GetFolder(connection.FolderName);

                            if (!folder.IsOpen)
                            {
                                WriteLineToLogStream("Opening folder '{0}'", folder.Name);
                                folder.Open(FolderAccess.ReadWrite);
                                folder.MessagesArrived += MessagesArrived;
                                folder.MessageFlagsChanged += MessageFlagsChanged;
                                //folder.CountChanged += CountChanged;
                                connection.Folder = folder;
                                WriteLineToLogStream("... opened");

                                // The first time we open a folder we always must check for new messages because
                                // in idle mode events are only generated when something changes in the folder
                                if (connection.CanIdle)
                                    CheckForNewMessages(connection);
                            }
                        }

                        if (!connection.CanIdle)
                            CheckForNewMessages(connection);

                        StartIdleorNoOpTask(connection);
                    }
                    catch (Exception exception)
                    {
                        // Only write one error each minute for a connection
                        if (connection.LastErrorTimeStamp.AddSeconds(60) < DateTime.Now)
                        {
                            connection.LastErrorTimeStamp = DateTime.Now;
                            WriteLineToLogStream(exception.Message);
                            CancelIdleorNoOpTask(connection);
                            CancelDownloadTask(connection);
                            connection.Client = null;
                            connection.Folder = null;
                            connection.IdlingOrNopTimeStamp = DateTime.MinValue;
                            connection.MailPollingTimeStamp = DateTime.MinValue;
                        }
                    }
                }
                Thread.Sleep(500);
            }

            WriteLineToLogStream("ConnectionWorker finished");
        }
        #endregion

        #region MessageProcessingTaskManager
        /// <summary>
        ///     This method monitors the <see cref="_connectionProcessingQueue" /> for newly added <see cref="Connection" />
        ///     objects. When an object is added the method tries to dequeue the object and then starts a new
        ///     <see cref="ImapMessageProcessingTask" />
        /// </summary>
        private void MessageProcessingTaskManager()
        {
            if (_debugMode) WriteLineToLogStream("MessageProcessingTaskManager started");

            var lastErrorTimeStamp = DateTime.MinValue;

            while (_running)
            {
                try
                {
                    Connection connection;

                    while (_connectionProcessingQueue.TryDequeue(out connection))
                    {
                        CancelIdleorNoOpTask(connection);
                        connection.DownloadTaskDone = new CancellationTokenSource();
                        var temp = connection;

                        switch (connection.Type)
                        {
                            case ClientType.Imap:
                                connection.DownloadTask = _taskFactory.StartNew(() => ImapMessageProcessingTask(temp),
                                    connection.DownloadTaskDone.Token, TaskCreationOptions.LongRunning,
                                    _taskFactory.Scheduler);
                                break;

                            case ClientType.Pop3:
                                connection.DownloadTask = _taskFactory.StartNew(() => Pop3MessageProcessingTask(temp),
                                    connection.DownloadTaskDone.Token, TaskCreationOptions.LongRunning,
                                    _taskFactory.Scheduler);
                                break;

                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (lastErrorTimeStamp.AddSeconds(60) < DateTime.Now)
                    {
                        lastErrorTimeStamp = DateTime.Now;
                        WriteLineToLogStream(exception.Message);
                    }
                }
                _newMailTrigger.WaitOne(100);
            }

            if (_debugMode) WriteLineToLogStream("MessageProcessingTaskManager finished");
        }
        #endregion

        #region ImapMessageProcessingTask
        /// <summary>
        ///     This method is called from the <see cref="MessageProcessingTaskManager" /> method and will process e-mails for
        ///     the given <see cref="ImapClient" /> <paramref name="connection" />
        /// </summary>
        /// <param name="connection"></param>
        private void ImapMessageProcessingTask(Connection connection)
        {
            var info = connection.UserName + " - " + connection.FolderName;

            try
            {
                if (_debugMode)
                    WriteLineToLogStream("{0} - ImapMessageProcessingTask started", info);

                var folder = connection.Folder;
                var searchQuery = connection.SearchQuery;

                lock (folder.SyncRoot)
                {
                    var uniqueIds = folder.Search(searchQuery);
                    var count = 0;
                    IMailFolder destinationFolder = null;

                    switch (connection.PostProcessing)
                    {
                        case PostProcessing.MoveToFolder:
                        case PostProcessing.FlagAsSeenAndMoveToFolder:
                            destinationFolder =
                                ((ImapClient)connection.Client).GetFolder(connection.DestinationFolderName);
                            break;
                    }

                    foreach (var uniqueId in uniqueIds)
                    {
                        try
                        {
                            var message = folder.GetMessage(uniqueId);

                            WriteLineToLogStream("{0} - Processing message with folder id {1} and subject '{2}'", info, uniqueId, message.Subject);

                            var subject = FileManager.RemoveInvalidFileNameChars(message.Subject);
                            if (string.IsNullOrWhiteSpace(subject))
                                subject = "Unnamed";

                            var fileName = FileManager.PathCombine(connection.OutputDirectory.FullName, subject) + ".eml";
                            fileName = FileManager.FileExistsMakeNew(fileName);

                            using (var file = File.Open(fileName, FileMode.Create))
                                message.WriteTo(file);

                            count += 1;

                            switch (connection.PostProcessing)
                            {
                                case PostProcessing.DeleteFromFolder:
                                    folder.AddFlags(uniqueId, MessageFlags.Seen | MessageFlags.Deleted, true);
                                    WriteLineToLogStream("{0} - Message with folder id {1} flagged as SEEN and DELETED", info, uniqueId);
                                    break;

                                case PostProcessing.MoveToFolder:
                                    folder.MoveTo(uniqueId, destinationFolder);
                                    break;

                                case PostProcessing.FlagAsSeenAndMoveToFolder:
                                    folder.AddFlags(uniqueId, MessageFlags.Seen, true);
                                    WriteLineToLogStream("{0} - Message with folder id {1} flagged as SEEN", info, uniqueId);
                                    folder.MoveTo(uniqueId, destinationFolder);
                                    WriteLineToLogStream("{0} - Message with folder id {1} moved to {2}", info, uniqueId, connection.DestinationFolderName);
                                    break;

                                case PostProcessing.FlagAsSeen:
                                    folder.AddFlags(uniqueId, MessageFlags.Seen, true);
                                    WriteLineToLogStream("{0} - Message with folder id {1} flagged as SEEN", info, uniqueId);
                                    break;

                                default:
                                    throw new ArgumentOutOfRangeException();
                            }

                            WriteLineToLogStream(
                                "{0} - Processing finished for message with folder id {1}, e-mail id {2} and subject '{3}'",
                                info, uniqueId, message.MessageId, message.Subject);

                            if (_maxDownloads != -1 && count == _maxDownloads)
                                break;
                        }
                        catch (MessageNotFoundException)
                        {
                            WriteLineToLogStream("{0} - The message with id {1} could not be processed", info, uniqueId);
                        }

                        if (connection.PostProcessing == PostProcessing.DeleteFromFolder)
                        {
                            folder.Expunge();
                            WriteLineToLogStream("{0} - Expunged messages that have been flagged as DELETED", info);
                        }
                    }

                    WriteLineToLogStream("{0} - {1} messages read", info, count);

                    // Check if there are any more items to process, if so then add the connection object back to the queue
                    var itemsLeft = folder.Search(searchQuery);
                    if (_running && itemsLeft.Count > 0)
                    {
                        WriteLineToLogStream("{0} - {1} left to process, adding connection back to queue", info, itemsLeft.Count);
                        _connectionProcessingQueue.Enqueue(connection);
                    }
                }
            }
            catch (Exception exception)
            {
                if (connection.LastErrorTimeStamp.AddSeconds(60) < DateTime.Now)
                    WriteLineToLogStream(exception.Message);
            }
            finally
            {
                connection.IsBusy = false;
            }

            if (_debugMode)
                WriteLineToLogStream("{0} - ImapMessageProcessingTask finished", info);
        }
        #endregion

        #region Pop3MessageProcessingTask
        /// <summary>
        ///     This method is called from the <see cref="MessageProcessingTaskManager" /> method and will process e-mails for
        ///     the given <see cref="Pop3Client" /> <paramref name="connection" />
        /// </summary>
        /// <param name="connection"></param>
        private void Pop3MessageProcessingTask(Connection connection)
        {
            var info = connection.UserName;

            try
            {
                if (_debugMode)
                    WriteLineToLogStream("{0} - Pop3MessageProcessingTask started", info);

                var client = (Pop3Client)connection.Client;

                lock (client.SyncRoot)
                {
                    var count = 0;
                    var messagesCounted = client.Count;
                    for (var i = 0; i < messagesCounted; i++)
                    {
                        try
                        {
                            var message = client.GetMessage(i);

                            WriteLineToLogStream("{0} - Processing message with id {1} and subject '{2}'", info, message.MessageId, message.Subject);

                            var subject = FileManager.RemoveInvalidFileNameChars(message.Subject);
                            if (string.IsNullOrWhiteSpace(subject))
                                subject = "Unnamed";

                            var fileName = FileManager.PathCombine(connection.OutputDirectory.FullName, subject) + ".eml";
                            fileName = FileManager.FileExistsMakeNew(fileName);

                            using (var file = File.Open(fileName, FileMode.Create))
                                message.WriteTo(file);

                            count += 1;

                            WriteLineToLogStream("{0} - Processing finished for message with id {1} and subject '{2}'",
                                info, message.MessageId, message.Subject);

                            // This only marks the messages for deletion, we need to disconnect to actually delete them
                            client.DeleteMessage(i);

                            if (_maxDownloads != -1 && count == _maxDownloads)
                                break;
                        }
                        catch (MessageNotFoundException)
                        {
                            WriteLineToLogStream("{0} - The message with id {1} could not be processed", info, i);
                        }
                    }

                    WriteLineToLogStream("{0} - {1} messages processed", info, count);

                    // Disconnect to delete the downloaded messages
                    client.Disconnect(true);
                    WriteLineToLogStream("{0} - Disconnected to delete the processed messages", info, count);

                    // Check if there are any more items to process, if so then add the connection object back to the queue
                    var itemsLeft = messagesCounted - count;
                    if (_running && itemsLeft > 0)
                    {
                        WriteLineToLogStream("{0} - {1} left to process", info, itemsLeft);
                        connection.MailPollingTimeStamp = DateTime.MinValue;
                    }
                }
            }
            catch (Exception exception)
            {
                if (connection.LastErrorTimeStamp.AddSeconds(60) < DateTime.Now)
                    WriteLineToLogStream(exception.Message);
            }
            finally
            {
                connection.IsBusy = false;
            }

            if (_debugMode)
                WriteLineToLogStream("{0} - Pop3MessageProcessingTask finished", info);
        }
        #endregion

        #region StartIdleorNoOpTask
        /// <summary>
        ///     Sends the <see cref="ImapClient.IdleAsync" /> or <see cref="MailStore.NoOpAsync" /> command to
        ///     the mailserver
        /// </summary>
        /// <param name="connection"></param>
        private void StartIdleorNoOpTask(Connection connection)
        {
            if (connection.IsBusy || _connectionProcessingQueue.Contains(connection)) return;
            if (connection.IdlingOrNopTimeStamp.AddSeconds(connection.IdleOrNopInterval) >= DateTime.Now) return;

            var client = (MailService)connection.Client;
            CancelIdleorNoOpTask(connection);

            lock (client.SyncRoot)
            {
                connection.IdleOrNoOpTaskDone = new CancellationTokenSource();

                // Only an Imap client can IDLE, so if this is true then we know that it is an Imap client
                if (connection.CanIdle)
                {
                    connection.IdleOrNoOpTask = ((ImapClient)client).IdleAsync(connection.IdleOrNoOpTaskDone.Token);
                    WriteLineToLogStream("Started idling for e-mail '{0}'", connection.UserName);
                }
                else
                {
                    connection.IdleOrNoOpTask = client.NoOpAsync(connection.IdleOrNoOpTaskDone.Token);
                    WriteLineToLogStream("Started noopping for e-mail '{0}'", connection.UserName);
                }

                connection.IdlingOrNopTimeStamp = DateTime.Now;
            }
        }
        #endregion

        #region CheckForNewMessages
        /// <summary>
        ///     Checks for new messages when the <see cref="Connection" /> is in <see cref="MailStore.NoOpAsync" /> mode
        /// </summary>
        /// <param name="connection"></param>
        private void CheckForNewMessages(Connection connection)
        {
            if (connection.IsBusy) return;
            if (connection.MailPollingTimeStamp.AddSeconds(connection.MailPollingInterval) > DateTime.Now) return;

            bool newMessagesFound;

            switch (connection.Type)
            {
                case ClientType.Imap:
                    var folder = connection.Folder;
                    var searchQuery = connection.SearchQuery;

                    lock (folder.SyncRoot)
                    {
                        var items = folder.Search(searchQuery);
                        newMessagesFound = items.Count > 0;
                    }

                    break;

                case ClientType.Pop3:
                    var pop3Client = (Pop3Client)connection.Client;

                    lock (pop3Client.SyncRoot)
                    {
                        newMessagesFound = pop3Client.Count > 0;
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            // We don't add the connection when it is already in the _connectionProcessingQueue
            if (newMessagesFound && !_connectionProcessingQueue.Contains(connection))
            {
                connection.IsBusy = true;
                _connectionProcessingQueue.Enqueue(connection);
                _newMailTrigger.Set();
            }

            connection.MailPollingTimeStamp = DateTime.Now;
        }
        #endregion

        #region CancelIdleorNoOpTask
        /// <summary>
        ///     Cancels the <see cref="Connection.IdleOrNoOpTask" />
        /// </summary>
        /// <param name="connection"></param>
        private static void CancelIdleorNoOpTask(Connection connection)
        {
            if (connection.IdleOrNoOpTask == null) return;

            if (connection.CanIdle)
            {
                WriteLineToLogStream("Canceling idling task for e-mail {0} and mailbox {1}", connection.UserName, connection.Folder);
            }
            else
            {
                WriteLineToLogStream("Canceling idling task for e-mail {0} and mailbox {1}", connection.UserName, connection.Folder);
            }

            try
            {
                connection.IdleOrNoOpTaskDone.Cancel();
                connection.IdleOrNoOpTask.Wait();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            connection.IdleOrNoOpTaskDone = null;
            connection.IdleOrNoOpTask = null;
            connection.IdlingOrNopTimeStamp = DateTime.MinValue;
        }
        #endregion

        #region CancelDownloadTask
        /// <summary>
        ///     Cancels the <see cref="Connection.DownloadTask" />
        /// </summary>
        /// <param name="connection"></param>
        private static void CancelDownloadTask(Connection connection)
        {
            if (connection.DownloadTask == null) return;

            WriteLineToLogStream("Canceling download task for e-mail {0} and mailbox {1}", connection.UserName, connection.Folder);
            connection.DownloadTaskDone.Cancel();

            try
            {
                connection.DownloadTask.Wait();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            connection.DownloadTaskDone = null;
            connection.DownloadTask = null;
        }
        #endregion

        #region Stop
        /// <summary>
        ///     Stops all the <see cref="_connections" /> and calls the <see cref="Dispose" /> method
        /// </summary>
        public void Stop()
        {
            _running = false;
            Dispose();
        }
        #endregion

        #region Dispose
        /// <summary>
        ///     Disconnects all the open <see cref="ImapClient" /> or <see cref="Pop3Client" />
        ///     <see cref="_connections"/> and releases all the used resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            WriteLineToLogStream("Disposing worker tasks");

            foreach (var task in _workerTasks)
            {
                try
                {
                    WriteLineToLogStream("Disposing task");
                    task.Wait();
                }
                catch (OperationCanceledException)
                {
                }
            }

            WriteLineToLogStream("Disposing connections");

            foreach (var connection in _connections)
            {
                WriteLineToLogStream("Disposing connection to host '{0}' on port {1} for user '{2}' and folder '{3}'",
                    connection.Host, connection.Port, connection.UserName, connection.FolderName);

                CancelIdleorNoOpTask(connection);
                CancelDownloadTask(connection);

                var folder = connection.Folder;
                if (folder != null)
                {
                    folder.MessagesArrived -= MessagesArrived;
                    folder.MessageFlagsChanged -= MessageFlagsChanged;
                    //folder.CountChanged -= CountChanged;
                    connection.Folder = null;
                }

                var client = (MailService)connection.Client;
                if (client != null && client.IsConnected)
                {
                    client.Disconnect(true);
                    client.Dispose();
                }
            }

            _disposed = true;
        }
        #endregion

        #region MessagesArrived
        /// <summary>
        ///     This method is fired when new messages arrive in the monitored folder
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <remarks>
        ///     Will only be fired when the <see cref="Connection.Client" /> is an <see cref="ImapClient" />
        ///     and supports the <see cref="ImapCapabilities.Idle" /> mode
        /// </remarks>
        private void MessagesArrived(object sender, MessagesArrivedEventArgs e)
        {
            var folder = (ImapFolder)sender;

            lock (folder.SyncRoot)
            {
                var connection = _connections.Find(m => m.Folder == folder);

                if (!connection.IsBusy && !_connectionProcessingQueue.Contains(connection))
                {
                    connection.IsBusy = true;
                    _connectionProcessingQueue.Enqueue(connection);
                    _newMailTrigger.Set();
                }

                WriteLineToLogStream("New message(s) received for e-mail {0}", connection.UserName);
            }
        }
        #endregion

        #region MessageFlagsChanged
        /// <summary>
        ///     This method is fired when a flag on a message is changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <remarks>
        ///     Will only be fired when the <see cref="Connection.Client" /> is an <see cref="ImapClient" />
        ///     and supports the <see cref="ImapCapabilities.Idle" /> mode
        /// </remarks>
        private void MessageFlagsChanged(object sender, MessageFlagsChangedEventArgs e)
        {
            var folder = (ImapFolder)sender;

            lock (folder.SyncRoot)
            {
                var connection = _connections.Find(m => m.Folder == folder);

                if (!connection.IsBusy && !_connectionProcessingQueue.Contains(connection))
                {
                    connection.IsBusy = true;
                    _connectionProcessingQueue.Enqueue(connection);
                    Thread.Sleep(100);
                    _newMailTrigger.Set();
                }
            }
        }
        #endregion

        #region WriteLineToLogStream
        /// <summary>
        ///     Writes a line and linefeed to the <see cref="_logStream" />
        /// </summary>
        /// <param name="message">The message to write</param>
        /// <param name="values">The parameters used in the <paramref name="message" /></param>
        private static void WriteLineToLogStream(string message, params object[] values)
        {
            if (_logStream == null) return;
            var line = DateTime.Now.ToString("s") + " - " + string.Format(message, values) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            _logStream.Write(bytes, 0, bytes.Length);
            _logStream.Flush();
            Console.Write(line);
        }
        #endregion
    }
}