using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MailKit.Search;
using MailKit.Security;

//
// Program.cs
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
    class Program
    {
        private static ConnectionManager _connectionManager;

        private static void Main(string[] args)
        {
            /*
            
            How to use this code...

            - 1: Create a connection manager
            - 2: Set an output stream for logging... or not
            - 3: Add an Imap connection to the ConnectionManager
            - 4: Start the connection manager

            ... and thats it.

            I tested this code with 40 mailboxes all in NOOP mode without any problems.

            */
            using (var outputStream = File.OpenWrite(@"d:\connectionmanager.txt"))
            using (_connectionManager = new ConnectionManager(outputStream, 10))
            {
                _connectionManager.AddImapConnection("username@example.com", "password", "imap.example.nl", 993,
                    SecureSocketOptions.Auto, "INBOX", SearchQuery.NotSeen, @"d:\somefolder", 300);

                _connectionManager.Start();
                Console.ReadKey();
                _connectionManager.Stop();
            }
            Console.WriteLine("ALL STOPPED");
            Console.ReadKey();
        }
    }
}
