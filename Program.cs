/*
 *  This source code is free for your use. It is a short example on
 *  how to set up a HL7 client for unsolicited messasges.
 *  
 * For demo uses all code is in one class, sometimes the code
 * will be inefficient to be more explanatory. Please keep
 * in mind that if you are building a application, work first
 * on a solid solution architecture and let this example be an
 * inspiration, nog a solution guideline.
 * 
 * Division By Zero (2013)
 * 
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Net;
using System.Net.Sockets;

using System.IO;
using NHapi.Base;
using NHapi.Base.Parser;
using NHapi.Base.Model;
using NHapi.Base.Util;


namespace NHapiMyFirstApp
{
    class HL7App
    {
        #region Defaults
        private const int DefaultPort = 1250;
        #endregion

        #region Private constants
        private const int MLLP_START_CHARACTER = 11; // HEX 0B
        private const int MLLP_FIRST_END_CHARACTER = 28; // HEX 1C
        private const int MLLP_LAST_END_CHARACTER = 13; // HEX 0D
        #endregion

        #region Private properties
        private bool continueProcessing = true;
        private TcpListener listener;
        #endregion

        #region Static method Main
        static void Main(string[] args)
        {
            int port = DefaultPort;
            if (args.Length == 1)
                int.TryParse(args[0], out port);

            Console.WriteLine("Starting HL7 client on port {0}.", port);
            Console.WriteLine("Press Ctrl-c to exit.");

            HL7App app = new HL7App();
            app.StartTCPListener(port);
        }

        private void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Console.WriteLine("Stopping...");

            continueProcessing = false;
            listener.Stop();
        }

        public static void MakeACK(IMessage inboundMessage, string ackCode, IMessage ackMessage)
        {
            MakeACK(inboundMessage, ackCode, ackMessage, null);
        }

        public static void MakeACK(IMessage inboundMessage, string ackCode, IMessage ackMessage, string errorMessage)
        {
            Terser t = new Terser(inboundMessage);

            ISegment inboundHeader = null;
            try
            {
                inboundHeader = t.getSegment("MSH");
            }
            catch (NHapi.Base.HL7Exception)
            {
                throw new NHapi.Base.HL7Exception("Need an MSH segment to create a response ACK");
            }

            MakeACK(inboundHeader, ackCode, ackMessage, errorMessage);
        }

        public static void MakeACK(ISegment inboundHeader, string ackCode, IMessage ackMessage, string errorMessage)
        {
            if (!inboundHeader.GetStructureName().Equals("MSH"))
                throw new NHapi.Base.HL7Exception("Need an MSH segment to create a response ACK (got " + inboundHeader.GetStructureName() + ")");

            // Find the HL7 version of the inbound message:
            string version = null;
            try
            {
                version = Terser.Get(inboundHeader, 12, 0, 1, 1);
            }
            catch (NHapi.Base.HL7Exception)
            {
                // I'm not happy to proceed if we can't identify the inbound
                // message version.
                throw new NHapi.Base.HL7Exception("Failed to get valid HL7 version from inbound MSH-12-1");
            }

            // Create a Terser instance for the outbound message (the ACK).
            Terser terser = new Terser(ackMessage);

            // Populate outbound MSH fields using data from inbound message
            ISegment outHeader = (ISegment)terser.getSegment("MSH");
            DeepCopy.copy(inboundHeader, outHeader);

            // Now set the message type, HL7 version number, acknowledgement code
            // and message control ID fields:
            string sendingApp = terser.Get("/MSH-3");
            string sendingEnv = terser.Get("/MSH-4");
            
            // Make sure you fill the MSH-3 and MSH-4 with the correct values
            // for you application, preferably with configuration
            terser.Set("/MSH-3", "HL7Client");
            terser.Set("/MSH-4", "EnvironmentIdentifier");
            terser.Set("/MSH-5", sendingApp);
            terser.Set("/MSH-6", sendingEnv);
            terser.Set("/MSH-7", DateTime.Now.ToString("yyyyMMddmmhh"));
            terser.Set("/MSH-9", "ACK");
            terser.Set("/MSH-12", version);
            terser.Set("/MSA-1", ackCode == null ? "AA" : ackCode);
            terser.Set("/MSA-2", Terser.Get(inboundHeader, 10, 0, 1, 1));

            // Set error message
            if (errorMessage != null)
                terser.Set("/ERR-7", errorMessage);
        }
        #endregion

        #region Private methods
        private void StartTCPListener(int port)
        {
            // Catch the break key press
            Console.CancelKeyPress += Console_CancelKeyPress;

            IPAddress ip = IPAddress.Any;
            listener = new TcpListener(ip, port);
            
            listener.Start();
            while (continueProcessing)
            {
                Console.WriteLine("Waiting for connection.");
                TcpClient client = listener.AcceptTcpClient();
                Console.WriteLine("Connection received.");

                HandleClient(client);
            }
        }

        private void HandleClient(TcpClient client)
        {
            string message = string.Empty;
            while (client.Connected)
            {
                // Read the next byte from the stream
                int b = client.GetStream().ReadByte();
                if (b == -1)
                {
                    // Client disconnected
                    client.Close();
                }

                // Start adding characters to the message
                // if the MLLP start character is received.
                if ((b == MLLP_START_CHARACTER) || (message.Length > 0))
                    message += (char) b;

                // Check if the message string ends with the two
                // MLLP end characters. If so, a complete HL7 message is
                // received.
                if ((message.Length > 3) && ((message[message.Length - 2] == MLLP_FIRST_END_CHARACTER) && (message[message.Length - 1] == MLLP_LAST_END_CHARACTER)))
                {
                    // String away the MLLP characters to keep the pure HL7 message
                    string hl7Message = message.Substring(1, message.Length - 3);

                    // Parse the HL7 message and get a response
                    string hl7Response = ParseHL7Message(hl7Message);

                    // Add MLLP characters to the response message
                    string responseMessage = (char)MLLP_START_CHARACTER + hl7Response + (char)MLLP_FIRST_END_CHARACTER + (char)MLLP_LAST_END_CHARACTER;
                    StreamWriter writer = new StreamWriter(client.GetStream());
                    writer.Write(responseMessage);
                    writer.Flush();
                }
                    
            }
            Console.WriteLine("Connection closed.");
        }

        private string ParseHL7Message(string message)
        {
            IMessage ackMessage = null;
            string result = string.Empty;
            PipeParser parser = new PipeParser();

            try
            {
                IMessage hl7Message = parser.Parse(message);

                string errorCode = "AA";
                string errorMessage = null;
                if (!ProcessMessage(hl7Message, out errorMessage))
                    errorCode = "AE";

                // Create a response message
                ackMessage = CreateACK(hl7Message, errorCode, errorMessage);
                result = parser.Encode(ackMessage);
            }
            catch (HL7Exception ex)
            {
                Console.WriteLine("Error while parsing: {0}", ex.Message);
            }

            return result;
        }

        private bool ProcessMessage(IMessage hl7Message, out string errorMessage)
        {
            errorMessage = null;

            Console.WriteLine("A HL7 message of the type {0} and version {1} is received.", hl7Message.GetStructureName(), hl7Message.Version);
            if (!hl7Message.GetStructureName().StartsWith("ADT_"))
            {
                errorMessage = "This message structure is not supported.";
                return false;
            }

            switch (hl7Message.Version)
            {
                case "2.3":
                    // Add code to handle the V2.3 of these ADT messages
                    NHapi.Model.V23.Segment.PID pid1 = (NHapi.Model.V23.Segment.PID) hl7Message.GetStructure("PID");
                    Console.WriteLine("PatientID {0}.", pid1.AlternatePatientID.ID.Value);
                    break;
                case "2.4":
                    // Add code to handle the V2.4 of these ADT messages
                    NHapi.Model.V24.Segment.PID pid2 = (NHapi.Model.V24.Segment.PID) hl7Message.GetStructure("PID");
                    Console.WriteLine("PatientID {0}.", pid2.PatientID.ID.Value);
                    break;
                default:
                    errorMessage = "This message version is not supported.";
                    return false;
            }

            return true;
        }

        private IMessage CreateACK(IMessage message, string returnCode, string errorMessage)
        {
            IMessage result = null;

            // Check the message version
            // to create an ACK object from the right
            // namespace
            switch (message.Version)
            {
                case "2.3":
                    result = new NHapi.Model.V23.Message.ACK();
                    break;
                case "2.4":
                    result = new NHapi.Model.V24.Message.ACK();
                    break;
                default:
                    throw new NotSupportedException("This HL7 version isn't supported.");
            }
            // An alternative to the switch statement here
            // is using reflection to load the right NHapi assembly
            // and create a new instance of the ACK class from there
            // This is better because it will work with each HL7 version
            // 
            //string ackClassType = string.Format("NHapi.Model.V{0}.Message.ACK, NHapi.Model.V{0}", message.Version.Remove(message.Version.IndexOf('.'), 1));
            //Type x = Type.GetType(ackClassType);
            //result = (IMessage)Activator.CreateInstance(x);
            
            // Fill the ACK message with the right values
            MakeACK(message, returnCode, result, errorMessage);

            return result;
        }
        #endregion
    }
}
