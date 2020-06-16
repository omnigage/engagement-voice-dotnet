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
        /// To use this example, you will need the following:
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
            VoiceTemplateResource humanRecording = new VoiceTemplateResource();
            humanRecording.name = "Human Recording";
            humanRecording.kind = "audio";
            humanRecording.filePath = ""; // Full path to audio file (e.g., /Users/Shared/piano.wav on Mac)

            //
            // 3. Define recording for machine trigger
            //
            VoiceTemplateResource machineRecording = new VoiceTemplateResource();
            machineRecording.name = "Machine Recording";
            machineRecording.kind = "audio";
            machineRecording.filePath = ""; // Full path to audio file (e.g., /Users/Shared/nimoy_spock.wav on Mac)

            //
            // 4. Set the caller ID for the voice activity
            //
            ActivityResource activity = new ActivityResource();
            activity.name = "Voice Blast";
            activity.kind = "voice";
            activity.callerIdId = ""; // UUID (e.g., yL9vQaWrSqg5W8EFEpE6xZ )

            //
            // 5. Define or more envelopes for populating the engagement queue
            //
            EnvelopeResource envelope = new EnvelopeResource();
            envelope.firstName = "";
            envelope.lastName = "";
            envelope.phoneNumber = ""; // In E.164 format (such as +1xxxxxxxxx)

            // Push one or more envelopes into list
            List<EnvelopeResource> envelopes = new List<EnvelopeResource> {};
            envelopes.Add(envelope);

            //
            // 6. Optionally, customize engagement name
            //
            EngagementResource engagement = new EngagementResource();
            engagement.name = "Example Voice Blast";
            engagement.direction = "outbound";

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
        /// Upload file, create an voice template
        /// </summary>
        /// <param name="tokenKey"></param>
        /// <param name="tokenSecret"></param>
        /// <param name="accountKey"></param>
        /// <param name="host"></param>
        /// <param name="filePaths"></param>
        /// <param name="body"></param>
        static async Task MainAsync(AuthContext auth, EngagementResource engagementInstance, ActivityResource activityInstance, VoiceTemplateResource humanRecording, VoiceTemplateResource machineRecording, List<EnvelopeResource> envelopes)
        {
            using (var client = new HttpClient())
            {
                // Build basic authorization
                auth.authorization = CreateAuthorization(auth.tokenKey, auth.tokenSecret);

                // Set request context for Omnigage API
                client.BaseAddress = new Uri(auth.host);
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + auth.authorization);
                client.DefaultRequestHeaders.Add("X-Account-Key", auth.accountKey);

                humanRecording.uploadId = await Upload(humanRecording.filePath, client);
                machineRecording.uploadId = await Upload(machineRecording.filePath, client);

                // Build `voice-template` instance payload and make request for human trigger
                string voiceTemplateHumanContent = CreateVoiceTemplateSchema(humanRecording);
                JObject voiceTemplateHumanResponse = await PostRequest(client, "voice-templates", voiceTemplateHumanContent);
                humanRecording.id = (string)voiceTemplateHumanResponse.SelectToken("data.id");

                // Build `voice-template` instance payload and make request for machine trigger
                string voiceTemplateMachineContent = CreateVoiceTemplateSchema(machineRecording);
                JObject voiceTemplateMachineResponse = await PostRequest(client, "voice-templates", voiceTemplateMachineContent);
                machineRecording.id = (string)voiceTemplateMachineResponse.SelectToken("data.id");

                Console.WriteLine($"Voice Template ID (human): {humanRecording.id}");
                Console.WriteLine($"Voice Template ID (machine): {machineRecording.id}");

                // Build `engagement` instance payload and make request
                string engagementContent = CreateEngagementSchema(engagementInstance);
                JObject engagementResponse = await PostRequest(client, "engagements", engagementContent);
                engagementInstance.id = (string)engagementResponse.SelectToken("data.id");

                // Build `activity` instance payload and make request
                activityInstance.engagementId = engagementInstance.id;
                string activityContent = CreateActivitySchema(activityInstance);
                JObject activityResponse = await PostRequest(client, "activities", activityContent);
                activityInstance.id = (string)activityResponse.SelectToken("data.id");

                Console.WriteLine($"Engagement ID: {engagementInstance.id}");
                Console.WriteLine($"Activity ID: {activityInstance.id}");

                // Define human trigger
                TriggerResource triggerHumanInstance = new TriggerResource();
                triggerHumanInstance.kind = "play";
                triggerHumanInstance.onEvent = "voice-human";
                triggerHumanInstance.activityId = activityInstance.id;
                triggerHumanInstance.voiceTemplateId = humanRecording.id;

                // Define machine trigger
                TriggerResource triggerMachineInstance = new TriggerResource();
                triggerMachineInstance.kind = "play";
                triggerMachineInstance.onEvent = "voice-machine";
                triggerMachineInstance.activityId = activityInstance.id;
                triggerMachineInstance.voiceTemplateId = machineRecording.id;

                // Build human `trigger` instance payload and make request
                string triggerContentForHuman = CreateTriggerSchema(triggerHumanInstance);
                JObject triggerResponseForHuman = await PostRequest(client, "triggers", triggerContentForHuman);
                triggerHumanInstance.id = (string)triggerResponseForHuman.SelectToken("data.id");

                // Build machine `trigger` instance payload and make request
                string triggerContentForMachine = CreateTriggerSchema(triggerMachineInstance);
                JObject triggerResponseForMachine = await PostRequest(client, "triggers", triggerContentForMachine);
                triggerMachineInstance.id = (string)triggerResponseForMachine.SelectToken("data.id");

                Console.WriteLine($"Trigger ID (human): {triggerHumanInstance.id}");
                Console.WriteLine($"Trigger ID (machine): {triggerMachineInstance.id}");

                // Set the engagement id on the envelopes
                foreach (var envelope in envelopes)
                {
                    envelope.engagementId = engagementInstance.id;
                }

                // Populate engagement queue
                PostBulkRequest(auth, "envelopes", CreateEnvelopeSchema(envelopes));

                // Schedule engagement for processing
                engagementInstance.status = "scheduled";
                string engagementScheduleContent = CreateEngagementSchema(engagementInstance);
                await PatchRequest(client, $"engagements/{engagementInstance.id}", engagementScheduleContent);
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
            JObject uploadResponse = await PostRequest(client, "uploads", uploadContent);

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
        /// Create a POST request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <param name="content"></param>
        /// <returns>JObject</returns>
        static async Task<JObject> PostRequest(HttpClient client, string uri, string content)
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
        static async Task<JObject> PatchRequest(HttpClient client, string uri, string content)
        {
            StringContent payload = new StringContent(content, Encoding.UTF8, "application/json");
            HttpResponseMessage request = await client.PatchAsync(uri, payload);
            string response = await request.Content.ReadAsStringAsync();
            return JObject.Parse(response);
        }

        /// <summary>
        /// Create a GET request to the Omnigage API and return an object for retrieving tokens
        /// </summary>
        /// <param name="client"></param>
        /// <param name="uri"></param>
        /// <returns>JObject</returns>
        static async Task<JObject> GetRequest(HttpClient client, string uri)
        {
            HttpResponseMessage response = await client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            return JObject.Parse(responseBody);
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
        /// Create Omnigage `/voice-templates` schema
        /// </summary>
        /// <param name="instance"></param>
        /// <returns>JSON</returns>
        static string CreateVoiceTemplateSchema(VoiceTemplateResource instance)
        {
            return @"{
                ""data"":{
                    ""attributes"":{
                        ""name"":""" + instance.name + @""",
                        ""kind"":""" + instance.kind + @"""
                    },
                    ""relationships"":{
                        ""upload"":{
                            ""data"": {
                                ""type"": ""uploads"",
                                ""id"": """ + instance.uploadId + @"""
                            }
                        }
                    },
                    ""type"":""voice-templates""
                }
            }";
        }

        /// <summary>
        /// Create Omnigage `/engagements` schema
        /// </summary>
        /// <param name="instance"></param>
        /// <returns>JSON</returns>
        static string CreateEngagementSchema(EngagementResource instance)
        {
            string id = "";
            if (instance.id != null)
            {
                id = $"\"id\": \"{instance.id}\",";
            }

            string status = "";
            if (instance.status != null)
            {
                status = $"\"status\": \"{instance.status}\",";
            }

            return @"{
                ""data"":{
                    " + id + @"
                    ""attributes"":{
                        " + status + @"
                        ""name"":""" + instance.name + @""",
                        ""direction"":""" + instance.direction + @"""
                    },
                    ""type"":""engagements""
                }
            }";
        }

        /// <summary>
        /// Create Omnigage `/activities` schema
        /// </summary>
        /// <param name="instance"></param>
        /// <returns>JSON</returns>
        static string CreateActivitySchema(ActivityResource instance)
        {
            return @"{
                ""data"":{
                    ""attributes"":{
                        ""name"":""" + instance.name + @""",
                        ""kind"":""" + instance.kind + @"""
                    },
                    ""relationships"":{
                        ""engagement"":{
                            ""data"": {
                                ""type"": ""engagements"",
                                ""id"": """ + instance.engagementId + @"""
                            }
                        },
                        ""caller-id"":{
                            ""data"": {
                                ""type"": ""caller-ids"",
                                ""id"": """ + instance.callerIdId + @"""
                            }
                        }
                    },
                    ""type"":""activities""
                }
            }";
        }

        /// <summary>
        /// Create Omnigage `/triggers` schema
        /// </summary>
        /// <param name="instance"></param>
        /// <returns>JSON</returns>
        static string CreateTriggerSchema(TriggerResource instance)
        {
            return @"{
                ""data"":{
                    ""attributes"":{
                        ""kind"":""" + instance.kind + @""",
                        ""on-event"":""" + instance.onEvent + @"""
                    },
                    ""relationships"":{
                        ""activity"":{
                            ""data"": {
                                ""type"": ""activities"",
                                ""id"": """ + instance.activityId + @"""
                            }
                        },
                        ""voice-template"":{
                            ""data"": {
                                ""type"": ""voice-templates"",
                                ""id"": """ + instance.voiceTemplateId + @"""
                            }
                        }
                    },
                    ""type"":""triggers""
                }
            }";
        }

        /// <summary>
        /// Create bulk Omnigage `/envelopes` schema
        /// </summary>
        /// <param name="envelopes"></param>
        /// <returns>JSON</returns>
        static string CreateEnvelopeSchema(List<EnvelopeResource> envelopes)
        {
            // Buld the serialized list of envelopes
            string instances = "";
            foreach (var instance in envelopes)
            {
                if (instances != "")
                {
                    instances += ",";
                }

                instances += @"{
                    ""attributes"":{
                        ""phone-number"":""" + instance.phoneNumber + @""",
                        ""meta"": {
                            ""first-name"":""" + instance.firstName + @""",
                            ""last-name"":""" + instance.lastName + @"""
                        }
                    },
                    ""relationships"":{
                        ""engagement"":{
                            ""data"": {
                                ""type"": ""engagements"",
                                ""id"": """ + instance.engagementId + @"""
                            }
                        }
                    },
                    ""type"":""envelopes""
                }";
            }

            // Create bulk envelope schema
            string envelopeRequestContent = @"{
                ""data"": [" + instances + @"]
            }";

            return envelopeRequestContent;
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

    public class VoiceTemplateResource
    {
        public string id;
        public string name;
        public string kind;
        public string uploadId;
        public string filePath;
    }

    public class EngagementResource
    {
        public string id;
        public string name;
        public string direction;
        public string status;
    }

    public class ActivityResource
    {
        public string id;
        public string name;
        public string kind;
        public string engagementId;
        public string callerIdId;
    }

    public class TriggerResource
    {
        public string id;
        public string kind;
        public string onEvent;
        public string voiceTemplateId;
        public string activityId;
    }

    public class EnvelopeResource
    {
        public string id;
        public string firstName;
        public string lastName;
        public string phoneNumber;
        public string engagementId;
    }
}