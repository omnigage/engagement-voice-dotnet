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
using Newtonsoft.Json;
using RestSharp;
using JsonApiSerializer;

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
            auth.TokenKey = "";
            auth.TokenSecret = "";

            // Retrieve from Account -> Settings -> General -> "Key" field
            auth.AccountKey = "";

            // API host path (e.g., https://api.omnigage.io/api/v1/)
            auth.Host = "";

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
            activity.CallerId = new CallerIdModel
            {
                Id = "" // UUID (e.g., yL9vQaWrSqg5W8EFEpE6xZ )
            };

            //
            // 5. Define or more envelopes for populating the engagement queue
            //
            EnvelopeModel envelope = new EnvelopeModel();
            envelope.PhoneNumber = ""; // In E.164 format (such as +1xxxxxxxxx)
            envelope.Meta = new Dictionary<string, string>
            {
                { "first-name", "" },
                { "last-name", "" }
            };

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
                // Set request context for Omnigage API
                client.BaseAddress = new Uri(auth.Host);
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + auth.Authorization);
                client.DefaultRequestHeaders.Add("X-Account-Key", auth.AccountKey);

                // Upload audio files and assign upload IDs
                humanRecording.Upload = new UploadModel
                {
                    Id = await Upload(humanRecording.FilePath, client)
                };
                machineRecording.Upload = new UploadModel
                {
                    Id = await Upload(machineRecording.FilePath, client)
                };

                // Create voice recording, which will be used for the `human` trigger
                await humanRecording.Create(client);

                // Create voice recording, to be used for the `machine` trigger
                await machineRecording.Create(client);

                Console.WriteLine($"Voice Template ID (human): {humanRecording.Id}");
                Console.WriteLine($"Voice Template ID (machine): {machineRecording.Id}");

                // Create engagement
                await engagement.Create(client);

                // Create activity
                activity.Engagement = engagement;
                await activity.Create(client);

                Console.WriteLine($"Engagement ID: {engagement.Id}");
                Console.WriteLine($"Activity ID: {activity.Id}");

                // Define human trigger
                TriggerModel triggerHumanInstance = new TriggerModel();
                triggerHumanInstance.Kind = "play";
                triggerHumanInstance.OnEvent = "voice-human";
                triggerHumanInstance.Activity = activity;
                triggerHumanInstance.VoiceTemplate = humanRecording;

                // Define machine trigger
                TriggerModel triggerMachineInstance = new TriggerModel();
                triggerMachineInstance.Kind = "play";
                triggerMachineInstance.OnEvent = "voice-machine";
                triggerMachineInstance.Activity = activity;
                triggerMachineInstance.VoiceTemplate = machineRecording;

                // Create triggers
                await triggerHumanInstance.Create(client);
                await triggerMachineInstance.Create(client);

                Console.WriteLine($"Trigger ID (human): {triggerHumanInstance.Id}");
                Console.WriteLine($"Trigger ID (machine): {triggerMachineInstance.Id}");

                // Set the engagement id on the envelopes
                foreach (var envelope in envelopes)
                {
                    envelope.Engagement = engagement;
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
            var bulkClient = new RestClient(auth.Host + uri);
            var request = new RestRequest(Method.POST);
            request.AddHeader("Accept", bulkRequestHeader);
            request.AddHeader("Content-Type", bulkRequestHeader);
            request.AddHeader("X-Account-Key", auth.AccountKey);
            request.AddHeader("Authorization", "Basic " + auth.Authorization);
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
        /// S3 Upload Failed exception
        /// </summary>
        public class S3UploadFailed : Exception { }
    }

    /// <summary>
    /// Authentication and request context
    /// </summary>
    public class AuthContext
    {
        public string TokenKey { get; set; }
        public string TokenSecret { get; set; }
        public string Host { get; set; }
        public string AccountKey { get; set; }

        /// <summary>
        /// Create Authorization token following RFC 2617 
        /// </summary>
        /// <returns>Base64 encoded string</returns>
        public string Authorization
        {
            get
            {
                byte[] authBytes = System.Text.Encoding.UTF8.GetBytes($"{this.TokenKey}:{this.TokenSecret}");
                return System.Convert.ToBase64String(authBytes);
            }
        }
        
    }

    /// <summary>
    /// Resource: `/voice-templates` - https://omnigage.docs.apiary.io/#reference/call-resources/voice-template
    /// </summary>
    public class VoiceTemplateModel : Adapter
    {
        public override string Type { get; } = "voice-templates";

        public string Name { get; set; }

        public string Kind { get; set; }

        public UploadModel Upload { get; set; }

        [JsonIgnore]
        public string FilePath { get; set; }
    }

    /// <summary>
    /// Resource `/engagements` - https://omnigage.docs.apiary.io/#reference/engagement-resources
    /// </summary>
    public class EngagementModel : Adapter
    {
        public override string Type { get; } = "engagements";

        public string Name;

        public string Direction;

        public string Status;
    }

    /// <summary>
    /// Resource: `/activities` - https://omnigage.docs.apiary.io/#reference/engagement-resources/activity-collection
    /// </summary>
    public class ActivityModel : Adapter
    {
        public override string Type { get; } = "activities";

        public string Name;

        public string Kind;

        public EngagementModel Engagement;

        [JsonProperty(propertyName: "caller-id")]
        public CallerIdModel CallerId;
    }

    /// <summary>
    /// Resource: `/triggers` - https://omnigage.docs.apiary.io/#reference/engagement-resources/trigger-collection
    /// </summary>
    public class TriggerModel : Adapter
    {
        public override string Type { get; } = "triggers";

        public string Kind;

        [JsonProperty(propertyName: "on-event")]
        public string OnEvent;

        [JsonProperty(propertyName: "voice-template")]
        public VoiceTemplateModel VoiceTemplate;

        public ActivityModel Activity;
    }

    /// <summary>
    /// Resource: `/envelopes` - https://omnigage.docs.apiary.io/#reference/engagement-resources/envelope-collection
    /// </summary>
    public class EnvelopeModel : Adapter
    {
        public override string Type { get; } = "envelopes";

        [JsonProperty(propertyName: "phone-number")]
        public string PhoneNumber;

        [JsonProperty(propertyName: "meta_prop")]
        public Dictionary<string, string> Meta;

        public EngagementModel Engagement;

        public static string SerializeBulk(List<EnvelopeModel> records)
        {
            string payload = JsonConvert.SerializeObject(records, new JsonApiSerializerSettings());
            // Work around `JsonApiSerializer` moving properties named "meta" above "attributes"
            return payload.Replace("meta_prop", "meta");
        }
    }

    /// <summary>
    /// Resource: `/uploads` - https://omnigage.docs.apiary.io/#reference/media-resources/upload
    /// </summary>
    public class UploadModel : Adapter
    {
        public override string Type { get; } = "uploads";
    }

    /// <summary>
    /// Resource: `/caller-ids` - https://omnigage.docs.apiary.io/#reference/identity-resources/caller-id-collection
    /// </summary>
    public class CallerIdModel : Adapter
    {
        public override string Type { get; } = "caller-ids";
    }

    /// <summary>
    /// Adapter for faciliating serializing model instances and making requests.
    /// </summary>
    abstract public class Adapter
    {
        public string Id { get; set; }
        public abstract string Type { get; }

        /// <summary>
        /// Serialize the current model instance.
        /// </summary>
        /// <returns>string</returns>
        public string Serialize()
        {
            return JsonConvert.SerializeObject(this, new JsonApiSerializerSettings());
        }

        /// <summary>
        /// Helper method for creating a new instance.
        /// </summary>
        /// <param name="client"></param>
        /// <returns>response</returns>
        public async Task<JObject> Create(HttpClient client)
        {
            string payload = this.Serialize();
            JObject response = await PostRequest(client, this.Type, payload);
            this.Id = (string)response.SelectToken("data.id");
            return response;
        }

        /// <summary>
        /// Helper method for updating an instance.
        /// </summary>
        /// <param name="client"></param>
        /// <returns>response</returns>
        public async Task<JObject> Update(HttpClient client)
        {
            string payload = this.Serialize();
            JObject response = await PatchRequest(client, $"{this.Type}/{this.Id}", payload);
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