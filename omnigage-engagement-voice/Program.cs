using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Omnigage.Auth;
using Omnigage.Resources;
using Omnigage.Util;

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
        private static async Task MainAsync(AuthContext auth, EngagementModel engagement, ActivityModel activity, VoiceTemplateModel humanRecording, VoiceTemplateModel machineRecording, List<EnvelopeModel> envelopes)
        {
            using (var client = new HttpClient())
            {
                // Set request context for Omnigage API
                client.BaseAddress = new Uri(auth.Host);
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + auth.Authorization);
                client.DefaultRequestHeaders.Add("X-Account-Key", auth.AccountKey);

                // Create upload instances
                UploadModel humanRecordingUpload = new UploadModel
                {
                    FilePath = humanRecording.FilePath
                };
                UploadModel machineRecordingUpload = new UploadModel
                {
                    FilePath = machineRecording.FilePath
                };

                await humanRecordingUpload.Create(client);
                await machineRecordingUpload.Create(client);

                // Add upload relationships to voice templates
                humanRecording.Upload = humanRecordingUpload;
                machineRecording.Upload = machineRecordingUpload;

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
                Adapter.PostBulkRequest(auth, "envelopes", EnvelopeModel.SerializeBulk(envelopes));

                // Schedule engagement for processing
                engagement.Status = "scheduled";
                await engagement.Update(client);
            };
        }
    }
}