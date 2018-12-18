using Kv6Receiver.bison;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Kv6Receiver
{
    class Program
    {
        static XmlSerializer serializer = new XmlSerializer(typeof(VV_TM_PUSH));
        static HttpClient httpClient = new HttpClient();

        static void Main(string[] args)
        {
            httpClient.BaseAddress = new Uri(args.Length < 1 ? "http://localhost:9200/qbuzz/" : args[0]);

            var nodeAddr = args.Length < 2 ? "tcp://pubsub.besteffort.ndovloket.nl:7658" : args[1];
            var subscriptions = args.Length < 3 ? new string[] { @"/QBUZZ/KV6posinfo" } : args.Skip(2).ToArray();

            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            var processor = StartProcessor(nodeAddr, subscriptions, cancellationTokenSource);

            Console.WriteLine("Processor running. Press Ctrl-C to exit...");
            try
            {
                processor.GetAwaiter().GetResult();
                Console.WriteLine("Processor completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Processor failed: {0}", ex);
            }
        }

        private static Task StartProcessor(string nodeAddr, string[] subscriptions, CancellationTokenSource cancellationTokenSource)
        {
            return Task.Factory.StartNew(async () =>
            {
                string index = "kv6";
                using (var socket = new SubscriberSocket())
                {
                    socket.Connect(nodeAddr);
                    try
                    {
                        subscriptions.All((s) =>
                        {
                            socket.Subscribe(s);
                            return true;
                        });

                        while (!cancellationTokenSource.IsCancellationRequested)
                        {
                            var messageList = socket.ReceiveMultipartBytes(2);
                            var msg1 = Encoding.UTF8.GetString(messageList[0]);

                            string msg2 = null;
                            using (GZipStream stream = new GZipStream(new MemoryStream(messageList[1]), CompressionMode.Decompress))
                            using (var sr = new StreamReader(stream))
                                msg2 = await sr.ReadToEndAsync();

#if DEBUG
                            Console.Write($"{msg1} - {msg2.Length} chars...");
#endif

                            var xml = DeserializeMessage(msg2);
                            var jsonObjects = GetJsonRepresentation(xml.Timestamp, xml.KV6posinfo);
                            await StoreJsonObjects(index, jsonObjects);
                        }
                    }
                    finally
                    {
                        socket.Disconnect(nodeAddr);
                        socket.Close();
                    }
                }

            }, cancellationTokenSource.Token);
        }

        private static Task StoreJsonObjects(string index, IEnumerable<JObject> jsonObjects)
        {
            jsonObjects.AsParallel().ForAll(async (jObj) =>
            {
                // Guid as ID
                var relativeUri = $"{index}/{Guid.NewGuid()}";
                var stringContent = jObj.ToString();
                var res = await httpClient.PostAsync(relativeUri, new StringContent(stringContent, Encoding.UTF8, "application/json"));
                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine("Write failure!");
                }
            });

            return Task.CompletedTask;
        }

        private static IEnumerable<JObject> GetJsonRepresentation(DateTime received, KV6posinfoType[] kV6posinfos)
        {
            /*
                    [System.Xml.Serialization.XmlElementAttribute("ARRIVAL", typeof(ARRIVALType))]
        [System.Xml.Serialization.XmlElementAttribute("DELAY", typeof(DELAYType))]
        [System.Xml.Serialization.XmlElementAttribute("DEPARTURE", typeof(DEPARTUREType))]
        [System.Xml.Serialization.XmlElementAttribute("END", typeof(ENDType))]
        [System.Xml.Serialization.XmlElementAttribute("INIT", typeof(INITType))]
        [System.Xml.Serialization.XmlElementAttribute("OFFROUTE", typeof(OFFROUTEType))]
        [System.Xml.Serialization.XmlElementAttribute("ONPATH", typeof(ONPATHType))]
        [System.Xml.Serialization.XmlElementAttribute("ONROUTE", typeof(ONROUTEType))]
        [System.Xml.Serialization.XmlElementAttribute("ONSTOP", typeof(ONSTOPType))]
        */
            foreach (var kv6obj in kV6posinfos.SelectMany(kv6pi => kv6pi.Items))
            {
                JObject jObj = null;
                string typeName = null;

                if (kv6obj is ARRIVALType kv6Arrival)
                {
                    jObj = JObject.FromObject(kv6Arrival);
                    typeName = "ARRIVAL";
                }
                else if (kv6obj is DELAYType kv6Delay)
                {
                    jObj = JObject.FromObject(kv6Delay);
                    typeName = "DELAY";
                }
                else if (kv6obj is DEPARTUREType kv6Departure)
                {
                    jObj = JObject.FromObject(kv6Departure);
                    typeName = "DEPARTURE";
                }
                else if (kv6obj is ENDType kv6End)
                {
                    jObj = JObject.FromObject(kv6End);
                    typeName = "END";
                }
                else if (kv6obj is INITType kv6Init)
                {
                    jObj = JObject.FromObject(kv6Init);
                    typeName = "INIT";
                }
                else if (kv6obj is OFFROUTEType kv6Offroute)
                {
                    jObj = JObject.FromObject(kv6Offroute);
                    typeName = "OFFROUTE";
                }
                else if (kv6obj is ONPATHType kv6Onpath)
                {
                    jObj = JObject.FromObject(kv6Onpath);
                    typeName = "ONPATH";
                }
                else if (kv6obj is ONROUTEType kv6Onroute)
                {
                    jObj = JObject.FromObject(kv6Onroute);
                    typeName = "ONROUTE";
                }
                else if (kv6obj is ONSTOPType kv6Onstop)
                {
                    jObj = JObject.FromObject(kv6Onstop);
                    typeName = "ONSTOP";
                }
                else
                    throw new ArgumentException($"Unexpected itemtype ({kv6obj.GetType().Name})", nameof(kV6posinfos));

                jObj.Add("type", JToken.FromObject(typeName));
                jObj.Add("received", JToken.FromObject(received));
                yield return jObj;
            }
        }

        private static VV_TM_PUSH DeserializeMessage(string msg)
        {
            using (var sr = new StringReader(msg))
            {
                return (VV_TM_PUSH)serializer.Deserialize(sr);
            }
        }
    }
}
