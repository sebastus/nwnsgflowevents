using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Net.Sockets;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NwNsgProject
{
    public static class Stage3QueueTrigger
    {
        [FunctionName("Stage3QueueTrigger")]
        public static async Task Run(
            [QueueTrigger("stage2", Connection = "AzureWebJobsStorage")]Chunk inputChunk,
            Binder binder, 
            Binder logTransmissions,
            TraceWriter log)
        {
//            log.Info($"C# Queue trigger function processed: {inputChunk}");

            string nsgSourceDataAccount = Util.GetEnvironmentVariable("nsgSourceDataAccount");
            if (nsgSourceDataAccount.Length == 0)
            {
                log.Error("Value for nsgSourceDataAccount is required.");
                throw new ArgumentNullException("nsgSourceDataAccount", "Please supply in this setting the name of the connection string from which NSG logs should be read.");
            }

            var attributes = new Attribute[]
            {
                new BlobAttribute(inputChunk.BlobName),
                new StorageAccountAttribute(nsgSourceDataAccount)
            };

            string nsgMessagesString;
            try
            {
                byte[] nsgMessages = new byte[inputChunk.Length];
                CloudBlockBlob blob = await binder.BindAsync<CloudBlockBlob>(attributes);
                await blob.DownloadRangeToByteArrayAsync(nsgMessages, 0, inputChunk.Start, inputChunk.Length);
                nsgMessagesString = System.Text.Encoding.UTF8.GetString(nsgMessages);
            }
            catch (Exception ex)
            {
                log.Error(string.Format("Error binding blob input: {0}", ex.Message));
                throw ex;
            }

            // skip past the leading comma
            string trimmedMessages = nsgMessagesString.Trim();
            int curlyBrace = trimmedMessages.IndexOf('{');
            string newClientContent = "{\"records\":[";
            newClientContent += trimmedMessages.Substring(curlyBrace);
            newClientContent += "]}";

            await SendMessagesDownstream(newClientContent, log);
        }

        public static async Task SendMessagesDownstream(string myMessages, TraceWriter log)
        {
            string outputBinding = Util.GetEnvironmentVariable("outputBinding");
            if (outputBinding.Length == 0)
            {
                log.Error("Value for outputBinding is required. Permitted values are: 'logstash'.");
                return;
            }

            switch (outputBinding)
            {
                case "logstash":
                    await obLogstash(myMessages, log);
                    break;
                case "arcsight":
                    await obArcsight(myMessages, log);
                    break;
            }
        }

        static async Task obArcsight(string newClientContent, TraceWriter log)
        {
            string arcsightAddress = Util.GetEnvironmentVariable("arcsightAddress");
            string arcsightPort = Util.GetEnvironmentVariable("arcsightPort");

            if (arcsightAddress.Length == 0 || arcsightPort.Length == 0)
            {
                log.Error("Values for arcsightAddress and arcsightPort are required.");
                return;
            }

            TcpClient client = new TcpClient(arcsightAddress, Convert.ToInt32(arcsightPort));
            NetworkStream stream = client.GetStream();

            int count = 0;
            Byte[] transmission = new Byte[] { };
            foreach (var message in convertToCEF(newClientContent, log))
            {

                try
                {
                    transmission = AppendToTransmission(transmission, message);

                    // batch up the messages
                    if (count++ == 1000)
                    {
                        await stream.WriteAsync(transmission, 0, transmission.Length);
                        count = 0;
                        transmission = new Byte[] { };
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Exception sending to ArcSight: {ex.Message}");
                }
            }
            if (count > 0)
            {
                try
                {
                    await stream.WriteAsync(transmission, 0, transmission.Length);
                }
                catch (Exception ex)
                {
                    log.Error($"Exception sending to ArcSight: {ex.Message}");
                }
            }
            await stream.FlushAsync();
        }

        static Byte[] AppendToTransmission(Byte[] existingMessages, string appendMessage)
        {
            Byte[] appendMessageBytes = Encoding.ASCII.GetBytes(appendMessage);
            Byte[] crlf = new Byte[] { 0x0D, 0x0A };

            Byte[] newMessages = new Byte[existingMessages.Length + appendMessage.Length + 2];

            existingMessages.CopyTo(newMessages, 0);
            appendMessageBytes.CopyTo(newMessages, existingMessages.Length);
            crlf.CopyTo(newMessages, existingMessages.Length + appendMessageBytes.Length);

            return newMessages;
        }

        public class SingleHttpClientInstance
        {
            private static readonly HttpClient HttpClient;

            static SingleHttpClientInstance()
            {
                HttpClient = new HttpClient();
                HttpClient.Timeout = new TimeSpan(0, 1, 0);
            }

            public static async Task<HttpResponseMessage> SendToLogstash(HttpRequestMessage req, TraceWriter log)
            {
                HttpResponseMessage response = null;
                var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                try
                {
                    response = await httpClient.SendAsync(req);
                }
                catch (AggregateException ex)
                {
                    log.Error("Got AggregateException.");
                    throw ex;
                }
                catch (TaskCanceledException ex)
                {
                    log.Error("Got TaskCanceledException.");
                    throw ex;
                }
                catch (Exception ex)
                {
                    log.Error("Got other exception.");
                    throw ex;
                }
                return response;
            }
        }

        static async Task obLogstash(string newClientContent, TraceWriter log)
        {
            string logstashAddress = Util.GetEnvironmentVariable("logstashAddress");
            string logstashHttpUser = Util.GetEnvironmentVariable("logstashHttpUser");
            string logstashHttpPwd = Util.GetEnvironmentVariable("logstashHttpPwd");

            if (logstashAddress.Length == 0 || logstashHttpUser.Length == 0 || logstashHttpPwd.Length == 0)
            {
                log.Error("Values for logstashAddress, logstashHttpUser and logstashHttpPwd are required.");
                return;
            }

            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback =
            new System.Net.Security.RemoteCertificateValidationCallback(
                delegate { return true; });

            // log.Info($"newClientContent: {newClientContent}");

            var client = new SingleHttpClientInstance();
            var creds = Convert.ToBase64String(System.Text.ASCIIEncoding.ASCII.GetBytes(string.Format("{0}:{1}", logstashHttpUser, logstashHttpPwd)));
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, logstashAddress);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Add("Authorization", "Basic " + creds);
                req.Content = new StringContent(newClientContent, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await SingleHttpClientInstance.SendToLogstash(req, log);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    log.Error($"StatusCode from Logstash: {response.StatusCode}, and reason: {response.ReasonPhrase}");
                }
            }
            catch (System.Net.Http.HttpRequestException e)
            {
                string msg = e.Message;
                if (e.InnerException != null)
                {
                    msg += " *** " + e.InnerException.Message;
                }
                log.Error($"HttpRequestException Error: \"{msg}\" was caught while sending to Logstash.");
                throw e;
            }
            catch (Exception f)
            {
                string msg = f.Message;
                if (f.InnerException != null)
                {
                    msg += " *** " + f.InnerException.Message;
                }
                log.Error($"Unknown error caught while sending to Logstash: \"{f.ToString()}\"");
                throw f;
            }
        }

        static System.Collections.Generic.IEnumerable<string> convertToCEF(string newClientContent, TraceWriter log)
        {
            // newClientContent is a json string with records

            NSGFlowLogRecords logs = JsonConvert.DeserializeObject<NSGFlowLogRecords>(newClientContent);

            string cefRecordBase = "";
            foreach (var record in logs.records)
            {
                cefRecordBase += record.MakeCEFTime();
                cefRecordBase += "|Microsoft.Network";
                cefRecordBase += "|NETWORKSECURITYGROUPS";
                cefRecordBase += "|" + record.properties.Version.ToString("0.0");
                cefRecordBase += "|" + record.category;
                cefRecordBase += "|" + record.operationName;
                cefRecordBase += "|0";  // severity is always 0
                cefRecordBase += "|deviceExternalId=" + record.MakeDeviceExternalID();

                string cefOuterFlowRecord = cefRecordBase;
                foreach (var outerFlows in record.properties.flows)
                {
                    cefOuterFlowRecord += " cs1=" + outerFlows.rule;

                    string cefInnerFlowRecord = cefOuterFlowRecord;
                    foreach (var innerFlows in outerFlows.flows)
                    {
                        var firstFlowTuple = new NSGFlowLogTuple(innerFlows.flowTuples[0]);
                        cefInnerFlowRecord += (firstFlowTuple.GetDirection == "I" ? " dmac=" : " smac=") + innerFlows.MakeMAC();

                        foreach (var flowTuple in innerFlows.flowTuples)
                        {
                            var tuple = new NSGFlowLogTuple(flowTuple);
                            yield return cefInnerFlowRecord + " " + tuple.ToString();
                        }
                    }
                }
            }
        }
    }
}
