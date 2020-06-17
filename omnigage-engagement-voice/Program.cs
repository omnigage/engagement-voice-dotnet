using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace omnigage_engagement_voice
{
    public class Program
    {
        /// <summary>
        /// To run this application, the following is required:
        ///
        /// - API token key/secret from Account -> Developer -> API Tokens
        /// - The account key from Account -> Settings -> General -> "Key" field
        /// - The API host (e.g., https://api.omnigage.io/api/v1/)
        /// - Two audio files (either wav or mp3)
        /// - A Caller ID UUID from Account -> Telephony -> Caller IDs -> Edit (in the URI)
        /// - Create one or more envelopes for populating the queue
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            //
            // 1. Setup authorization context
            //
            AuthContext auth = new AuthContext();

            // Set token retrieved from Account -> Developer -> API Tokens
            auth.tokenKey = "";
            auth.tokenSecret = "";

            // Retrieve from Account -> Settings -> General -> "Key" field
            auth.accountKey = "";

            // API host path (e.g., https://api.omnigage.io/api/v1/)
            auth.host = "";

            //
            // 2. Define recording for human trigger
            //
            VoiceTemplateModel humanRecording = new VoiceTemplateModel();
            humanRecording.Name = "Human Recording";
            humanRecording.Kind = "audio";
            humanRecording.FilePath = ""; // Full path to audio file (e.g., /Users/Shared/piano.wav on Mac)

            //
            // 3. Define recording for machine trigger
            //
            VoiceTemplateModel machineRecording = new VoiceTemplateModel();
            machineRecording.Name = "Machine Recording";
            machineRecording.Kind = "audio";
            machineRecording.FilePath = ""; // Full path to audio file (e.g., /Users/Shared/nimoy_spock.wav on Mac)

            //
            // 4. Set the caller ID for the voice activity
            //
            ActivityModel activity = new ActivityModel();
            activity.Name = "Voice Blast";
            activity.Kind = "voice";
            activity.CallerIdId = ""; // UUID (e.g., yL9vQaWrSqg5W8EFEpE6xZ )

            //
            // 5. Define or more envelopes for populating the engagement queue
            //
            EnvelopeModel envelope = new EnvelopeModel();
            envelope.FirstName = "";
            envelope.LastName = "";
            envelope.PhoneNumber = ""; // In E.164 format (such as +1xxxxxxxxx)

            // Push one or more envelopes into list
            List<EnvelopeModel> envelopes = new List<EnvelopeModel> { };
            envelopes.Add(envelope);

            //
            // 6. Optionally, customize engagement name
            //
            EngagementModel engagement = new EngagementModel();
            engagement.Name = "Example Voice Blast";
            engagement.Direction = "outbound";

            try
            {
                MainAsync(auth, engagement, activity, humanRecording, machineRecording, envelopes).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Create two voice templates, an engagement and populate the queue.
        /// </summary>
        /// <param name="auth"></param>
        /// <param name="engagement"></param>
        /// <param name="activity"></param>
        /// <param name="humanRecording"></param>
        /// <param name="machineRecording"></param>
        /// <param name="envelopes"></param>
        /// <returns></returns>
        static async Task MainAsync(AuthContext auth, EngagementModel engagement, ActivityModel activity, VoiceTemplateModel humanRecording, VoiceTemplateModel machineRecording, List<EnvelopeModel> envelopes)
        {
            using (var client = new HttpClient())
            {
                // Build basic authorization
                auth.authorization = CreateAuthorization(auth.tokenKey, auth.tokenSecret);

                // Set request context for Omnigage API
                client.BaseAddress = new Uri(auth.host);
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + auth.authorization);
                client.DefaultRequestHeaders.Add("X-Account-Key", auth.accountKey);

                // Upload audio files and assign upload IDs
                humanRecording.UploadId = await Upload(humanRecording.FilePath, client);
                machineRecording.UploadId = await Upload(machineRecording.FilePath, client);

                // Create voice recording, which will be used for the `human` trigger
                await humanRecording.Create(client);

                // Create voice recording, to be used for the `machine` trigger
                await machineRecording.Create(client);

                Console.WriteLine($"Voice Template ID (human): {humanRecording.Id}");
                Console.WriteLine($"Voice Template ID (machine): {machineRecording.Id}");

                await engagement.Create(client);

                // Build `activity` instance payload and make request
                activity.EngagementId = engagement.Id;
                await activity.Create(client);

                Console.WriteLine($"Engagement ID: {engagement.Id}");
                Console.WriteLine($"Activity ID: {activity.Id}");

                // Define human trigger
                TriggerModel triggerHumanInstance = new TriggerModel();
                triggerHumanInstance.Kind = "play";
                triggerHumanInstance.OnEvent = "voice-human";
                triggerHumanInstance.ActivityId = activity.Id;
                triggerHumanInstance.VoiceTemplateId = humanRecording.Id;
                await triggerHumanInstance.Create(client);

                // Define machine trigger
                TriggerModel triggerMachineInstance = new TriggerModel();
                triggerMachineInstance.Kind = "play";
                triggerMachineInstance.OnEvent = "voice-machine";
                triggerMachineInstance.ActivityId = activity.Id;
                triggerMachineInstance.VoiceTemplateId = machineRecording.Id;
                await triggerMachineInstance.Create(client);

                Console.WriteLine($"Trigger ID (human): {triggerHumanInstance.Id}");
                Console.WriteLine($"Trigger ID (machine): {triggerMachineInstance.Id}");

                // Set the engagement id on the envelopes
                foreach (var envelope in envelopes)
                {
                    envelope.EngagementId = engagement.Id;
                }

                // Populate engagement queue
                PostBulkRequest(auth, "envelopes", EnvelopeModel.SerializeBulk(envelopes));

                // Schedule engagement for processing
                engagement.Status = "scheduled";
                await engagement.Update(client);
            };
        }

        /// <summary>
        /// Create Omnigage upload instance for signing S3 request. Upload `filePath` to S3 and return
        /// `upload` id to be used on another instance.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="client"></param>
        /// <returns>Upload ID</returns>
        static async Task<string> Upload(string filePath, HttpClient client)
        {
            // Check that the file exists
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File {filePath} not found.");
            }

            // Collect meta on the file
            string fileName = Path.GetFileName(filePath);
            long fileSize = new System.IO.FileInfo(filePath).Length;
            string mimeType = GetMimeType(fileName);

            // Ensure proper MIME type
            if (mimeType == null)
            {
                throw new System.InvalidOperationException("Only WAV or MP3 files accepted.");
            }

            // Build `upload` instance payload and make request
            string uploadContent = CreateUploadSchema(fileName, mimeType, fileSize);
            JObject uploadResponse = await Adapter.PostRequest(client, "uploads", uploadContent);

            // Extract upload ID and request URL
            string uploadId = (string)uploadResponse.SelectToken("data.id");
            string requestUrl = (string)uploadResponse.SelectToken("data.attributes.request-url");

            Console.WriteLine($"Upload ID: {uploadId}");

            using (var clientS3 = new HttpClient())
            {
                // Create multipart form including setting form data and file content
                MultipartFormDataContent form = await CreateMultipartForm(uploadResponse, filePath, fileName, mimeType);

                // Upload to S3
                await PostS3Request(clientS3, uploadResponse, form, requestUrl);

                return uploadId;
            };
        }
        
        /// <summary>
        /// Create a bulk request to the Omnigage API and return an object
        /// </summary>
        /// <param name="auth"></param>
        /// <param name="uri"></param>
        /// <param name="payload"></param>
        /// <returns>IRestResponse</returns>
        static IRestResponse PostBulkRequest(AuthContext auth, string uri, string payload)
        {
            string bulkRequestHeader = "application/vnd.api+json;ext=bulk";
            var bulkClient = new RestClient(auth.host + uri);
            var request = new RestRequest(Method.POST);
            request.AddHeader("Accept", bulkRequestHeader);
            request.AddHeader("Content-Type", bulkRequestHeader);
            request.AddHeader("X-Account-Key", auth.accountKey);
            request.AddHeader("Authorization", "Basic " + auth.authorization);
            request.AddParameter(bulkRequestHeader, payload, ParameterType.RequestBody);
            return bulkClient.Execute(request);
        }

        /// <summary>
        /// Make a POST request to S3 using presigned headers and multipart form
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uploadInstance"></param>
        /// <param name="form"></param>
        /// <param name="url"></param>
        static async Task PostS3Request(HttpClient client, JObject uploadInstance, MultipartFormDataContent form, string url)
        {
            object[] requestHeaders = uploadInstance.SelectToken("data.attributes.request-headers").Select(s => (object)s).ToArray();

            // Set each of the `upload` instance headers
            foreach (JObject header in requestHeaders)
            {
                foreach (KeyValuePair<string, JToken> prop in header)
                {
                    client.DefaultRequestHeaders.Add(prop.Key, (string)prop.Value);
                }
            }

            // Make S3 request
            HttpResponseMessage responseS3 = await client.PostAsync(url, form);
            string responseContent = await responseS3.Content.ReadAsStringAsync();

            if ((int)responseS3.StatusCode == 204)
            {
                Console.WriteLine("Successfully uploaded file.");
            }
            else
            {
                Console.WriteLine(responseS3);
                throw new S3UploadFailed();
            }
        }

        /// <summary>
        /// Create a multipart form using form data from the Omnigage `upload` instance along with the specified file path.
        /// </summary>
        /// <param name="uploadInstance"></param>
        /// <param name="filePath"></param>
        /// <param name="fileName"></param>
        /// <param name="mimeType"></param>
        /// <returns>A multipart form</returns>
        static async Task<MultipartFormDataContent> CreateMultipartForm(JObject uploadInstance, string filePath, string fileName, string mimeType)
        {
            // Retrieve values to use for uploading to S3
            object[] requestFormData = uploadInstance.SelectToken("data.attributes.request-form-data").Select(s => (object)s).ToArray();

            MultipartFormDataContent form = new MultipartFormDataContent("Upload----" + DateTime.Now.ToString(CultureInfo.InvariantCulture));

            // Set each of the `upload` instance form data
            foreach (JObject formData in requestFormData)
            {
                foreach (KeyValuePair<string, JToken> prop in formData)
                {
                    form.Add(new StringContent((string)prop.Value), prop.Key);
                }
            }

            // Set the content type (required by presigned URL)
            form.Add(new StringContent(mimeType), "Content-Type");

            // Add file content to form
            ByteArrayContent fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(filePath));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
            form.Add(fileContent, "file", fileName);

            return form;
        }

        /// <summary>
        /// Determine MIME type based on the file name.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns>MIME type</returns>
        static string GetMimeType(string fileName)
        {
            string extension = Path.GetExtension(fileName);

            if (extension == ".wav")
            {
                return "audio/wav";
            }
            else if (extension == ".mp3")
            {
                return "audio/mpeg";
            }

            return null;
        }

        /// <summary>
        /// Create Omnigage `uploads` schema
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="mimeType"></param>
        /// <param name="fileSize"></param>
        /// <returns>JSON</returns>
        static string CreateUploadSchema(string fileName, string mimeType, long fileSize)
        {
            return @"{
                'name': '" + fileName + @"',
                'type': '" + mimeType + @"',
                'size': " + fileSize + @"
            }";
        }

        /// <summary>
        /// Create Authorization token following RFC 2617 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="secret"></param>
        /// <returns>Base64 encoded string</returns>
        static string CreateAuthorization(string key, string secret)
        {
            byte[] authBytes = System.Text.Encoding.UTF8.GetBytes($"{key}:{secret}");
            return System.Convert.ToBase64String(authBytes);
        }

        /// <summary>
        /// S3 Upload Failed exception
        /// </summary>
        public class S3UploadFailed : Exception { }
    }

    public class AuthContext
    {
        public string tokenKey;
        public string tokenSecret;
        public string host;
        public string accountKey;
        public string authorization;
    }

    public class VoiceTemplateModel : Adapter
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string UploadId { get; set; }
        public string FilePath { get; set; }

        public override string Type { get { return "voice-templates"; } }

        public override string Serialize()
        {
            return @"{
                ""attributes"":{
                    ""name"":""" + this.Name + @""",
                    ""kind"":""" + this.Kind + @"""
                },
                ""relationships"":{
                    ""upload"":{
                        ""data"": {
                            ""type"": ""uploads"",
                            ""id"": """ + this.UploadId + @"""
                        }
                    }
                },
                ""type"":""voice-templates""
            }";
        }
    }

    public class EngagementModel : Adapter
    {
        public string Name;
        public string Direction;
        public string Status;

        public override string Type { get { return "engagements"; } }

        public override string Serialize()
        {
            string id = "";
            if (this.Id != null)
            {
                id = $"\"id\": \"{this.Id}\",";
            }

            string status = "";
            if (this.Status != null)
            {
                status = $"\"status\": \"{this.Status}\",";
            }

            return @"{
                " + id + @"
                ""attributes"":{
                    " + status + @"
                    ""name"":""" + this.Name + @""",
                    ""direction"":""" + this.Direction + @"""
                },
                ""type"":""engagements""
            }";
        }
    }

    public class ActivityModel : Adapter
    {
        public string Name;
        public string Kind;
        public string EngagementId;
        public string CallerIdId;

        public override string Type { get { return "activities"; } }

        public override string Serialize()
        {
            return @"{
                ""attributes"":{
                    ""name"":""" + this.Name + @""",
                    ""kind"":""" + this.Kind + @"""
                },
                ""relationships"":{
                    ""engagement"":{
                        ""data"": {
                            ""type"": ""engagements"",
                            ""id"": """ + this.EngagementId + @"""
                        }
                    },
                    ""caller-id"":{
                        ""data"": {
                            ""type"": ""caller-ids"",
                            ""id"": """ + this.CallerIdId + @"""
                        }
                    }
                },
                ""type"":""activities""
            }";
        }
    }

    public class TriggerModel : Adapter
    {
        public string Kind;
        public string OnEvent;
        public string VoiceTemplateId;
        public string ActivityId;

        public override string Type { get { return "triggers"; } }

        public override string Serialize()
        {
            return @"{
                ""attributes"":{
                    ""kind"":""" + this.Kind + @""",
                    ""on-event"":""" + this.OnEvent + @"""
                },
                ""relationships"":{
                    ""activity"":{
                        ""data"": {
                            ""type"": ""activities"",
                            ""id"": """ + this.ActivityId + @"""
                        }
                    },
                    ""voice-template"":{
                        ""data"": {
                            ""type"": ""voice-templates"",
                            ""id"": """ + this.VoiceTemplateId + @"""
                        }
                    }
                },
                ""type"":""triggers""
            }";
        }
    }

    public class EnvelopeModel : Adapter
    {
        public string FirstName;
        public string LastName;
        public string PhoneNumber;
        public string EngagementId;

        public override string Type { get { return "envelopes"; } }

        public override string Serialize()
        {
            return @"{
                ""attributes"":{
                    ""phone-number"":""" + this.PhoneNumber + @""",
                    ""meta"": {
                        ""first-name"":""" + this.FirstName + @""",
                        ""last-name"":""" + this.LastName + @"""
                    }
                },
                ""relationships"":{
                    ""engagement"":{
                        ""data"": {
                            ""type"": ""engagements"",
                            ""id"": """ + this.EngagementId + @"""
                        }
                    }
                },
                ""type"":""envelopes""
            }";
        }

        public static string SerializeBulk(List<EnvelopeModel> records)
        {
            // Build the serialized list
            string instances = "";
            foreach (var instance in records)
            {
                if (instances != "")
                {
                    instances += ",";
                }

                instances += instance.Serialize();
            }

            // Create bulk envelope schema
            string payload = @"{
                ""data"": [" + instances + @"]
            }";

            return payload;
        }
    }

    abstract public class Adapter
    {
        public string Id { get; set; }
        public abstract string Serialize();
        public abstract string Type { get; }

        /// <summary>
        /// Serialize wrapper for payload. In JSON:API, this is a "data" envelope.
        /// </summary>
        /// <param name="payload"></param>
        /// <returns></returns>
        static public string SerializePayload(string payload)
        {
            return @"{
                ""data"": " + payload + @"
            }";
        }

        /// <summary>
        /// Helper method for creating a new instance.
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        public async Task<JObject> Create(HttpClient client)
        {
            string payload = this.Serialize();
            JObject response = await PostRequest(client, this.Type, SerializePayload(payload));
            this.Id = (string)response.SelectToken("data.id");
            return response;
        }

        /// <summary>
        /// Helper method for updating an instance.
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        public async Task<JObject> Update(HttpClient client)
        {
            string payload = this.Serialize();
            JObject response = await PatchRequest(client, $"{this.Type}/{this.Id}", SerializePayload(payload));
            return response;
        }

        /// <summary>
        /// Create a POST request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <param name="content"></param>
        /// <returns>JObject</returns>
        public static async Task<JObject> PostRequest(HttpClient client, string uri, string content)
        {
            StringContent payload = new StringContent(content, Encoding.UTF8, "application/json");
            HttpResponseMessage request = await client.PostAsync(uri, payload);
            string response = await request.Content.ReadAsStringAsync();
            return JObject.Parse(response);
        }


        /// <summary>
        /// Create a PATCH request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <param name="content"></param>
        /// <returns>JObject</returns>
        public static async Task<JObject> PatchRequest(HttpClient client, string uri, string content)
        {
            StringContent payload = new StringContent(content, Encoding.UTF8, "application/json");
            HttpResponseMessage request = await client.PatchAsync(uri, payload);
            string response = await request.Content.ReadAsStringAsync();
            return JObject.Parse(response);
        }
    }
}