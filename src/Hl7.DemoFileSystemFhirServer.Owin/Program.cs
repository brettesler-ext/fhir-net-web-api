﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Hl7.Fhir.DemoFileSystemFhirServer;

namespace Hl7.DemoFileSystemFhirServer
{
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    class Program
    {
        static private IDisposable _fhirServerController;
        static public string _baseAddress;

        static void Main(string[] args)
        {
            // Ensure that we grab an available IP port on the local workstation
            // http://stackoverflow.com/questions/9895129/how-do-i-find-an-available-port-before-bind-the-socket-with-the-endpoint
            string port = "9000";

            using (Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                sock.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.1"), int.Parse(port))); // Pass 0 here, it means to go looking for a free port
                port = ((IPEndPoint)sock.LocalEndPoint).Port.ToString();
                sock.Close();
            }

            // Now use that randomly located port to start up a local FHIR server
            _baseAddress = "http://localhost:" + port + "/";
            _fhirServerController = Microsoft.Owin.Hosting.WebApp.Start<Startup>(_baseAddress);

            // Inititalize the server
            Console.WriteLine($"Initialize the FHIR server");
            Console.WriteLine($"BaseURI: {_baseAddress}");
            Console.WriteLine($"Path: {DirectorySystemService<System.Web.Http.Dependencies.IDependencyScope>.Directory}");


            // Wait for the console to be Completed
            Console.WriteLine();
            Console.WriteLine("Press any key to end the FHIR server ...");
            Console.ReadKey();

            if (_fhirServerController != null)
                _fhirServerController.Dispose();
        }
    }
}
