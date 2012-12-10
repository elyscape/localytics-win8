using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.System.Profile;

namespace Localytics
{
    public class LocalyticsSession
    {
        #region library constants
        private const int maxStoredSessions = 10;
        private const int maxNameLength = 100;
        private const string libraryVersion = "windowsphone_2.2";
        private const string directoryName = "localytics";
        private const string sessionFilePrefix = "s_";
        private const string uploadFilePrefix = "u_";
        private const string metaFileName = "m_meta";

        private const string serviceURLBase = "http://analytics.localytics.com/api/v2/applications/";
        #endregion

        #region private members
        private string appKey;
        private string sessionUuid;
        private string sessionFilename;
        private bool isSessionOpen = false;
        private bool isSessionClosed = false;
        private double sessionStartTime = 0;
        #endregion

        #region static members
        private static bool isUploading = false;
        private static StorageFolder localStorage = null;
        #endregion

        #region private methods

        #region Storage
        /// <summary>
        /// Caches the reference to the app's isolated storage
        /// </summary>
        private static StorageFolder getStore()
        {
            if (localStorage == null)
            {
                localStorage = ApplicationData.Current.LocalFolder;
            }

            return localStorage;
        }

        /// <summary>
        /// Tallies up the number of files whose name starts w/ sessionFilePrefix in the localytics dir
        /// </summary>
        private static async Task<int> GetNumberOfStoredSessions()
        {
            var store = getStore();
            try
            {
                var folder = await store.GetFolderAsync(directoryName);
                var files = await folder.GetFilesAsync();
                return files.Count(x => x.Name.StartsWith(sessionFilePrefix));
            }
            catch (FileNotFoundException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets a stream pointing to the requested file.  If the file does not exist it is created.
        /// </summary>
        /// <param name="filename">Name of the file (w/o directory) to create</param>
        private static async Task<Stream> GetStreamForFile(string filename)
        {
            var store = getStore();
            var folder = await store.CreateFolderAsync(directoryName, CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync(filename, CreationCollisionOption.OpenIfExists);
            return await file.OpenStreamForWriteAsync();
        }

        /// <summary>
        /// Appends a string to the end of a text file.
        /// </summary>
        /// <param name="text">Text to append</param>
        /// <param name="filename">Name of file to append to</param>
        private static async Task AppendTextToFile(string text, string filename)
        {
            using (var file = await GetStreamForFile(filename))
            {
                file.Seek(0, SeekOrigin.End);
                using (var writer = new StreamWriter(file))
                {
                    writer.Write(text);
                    writer.Flush();
                }
            }
        }

        /// <summary>
        /// Reads a file and returns its contents as a string
        /// </summary>
        /// <param name="filename">file to read (w/o directory prefix)</param>
        /// <returns>the contents of the file</returns>
        private static async Task<string> GetFileContents(string filename)
        {
            var store = getStore();
            using (var file = await store.OpenStreamForReadAsync(directoryName + @"\" + filename))
            {
                using (var reader = new StreamReader(file))
                {
                    return reader.ReadToEnd();
                }
            }
        }
        #endregion

        #region upload

        /// <summary>
        /// Goes through all the upload files and collects their contents for upload
        /// </summary>
        /// <returns>A string containing the concatenated </returns>
        private static async Task<string> GetUploadContents()
        {
            StringBuilder contents = new StringBuilder();
            var store = getStore();

            var folder = await store.CreateFolderAsync(directoryName, CreationCollisionOption.OpenIfExists);

            var files = await folder.GetFilesAsync();
            foreach (var file in files.Where(x => x.Name.StartsWith(uploadFilePrefix)))
            {
                contents.Append(await GetFileContents(file.Name));
            }

            return contents.ToString();
        }

        /// <summary>
        /// loops through all the files in the directory deleting the upload files
        /// </summary>
        private static async Task DeleteUploadFiles()
        {
            var store = getStore();
            var folder = await store.CreateFolderAsync(directoryName, CreationCollisionOption.OpenIfExists);

            var files = await folder.GetFilesAsync();
            foreach (var file in files.Where(x => x.Name.StartsWith(uploadFilePrefix)))
            {
                await file.DeleteAsync();
            }
        }

        /// <summary>
        /// Rename any open session files. This way events recorded during uploaded get written safely to disk
        /// and threading difficulties are missed.
        /// </summary>
        private async Task RenameOrAppendSessionFiles()
        {
            var store = getStore();
            var folder = await store.CreateFolderAsync(directoryName, CreationCollisionOption.OpenIfExists);
            var files = await folder.GetFilesAsync();
            bool addedHeader = false;

            string destinationFileName = uploadFilePrefix + Guid.NewGuid().ToString();
            foreach (var file in files.Where(x => x.Name.StartsWith(sessionFilePrefix)))
            {
                // Any time sessions are appended, an upload header should be added. But only one is needed regardless of number of files added
                if (!addedHeader)
                {
                    await AppendTextToFile(await GetBlobHeader(), destinationFileName);
                    addedHeader = true;
                }
                await AppendTextToFile(await GetFileContents(file.Name), destinationFileName);
                await file.DeleteAsync();
            }
        }

        /// <summary>
        /// Runs on a seperate thread and is responsible for renaming and uploading files as appropriate
        /// </summary>
        private async Task BeginUpload()
        {
            LogMessage("Beginning upload.");

            try
            {
                await RenameOrAppendSessionFiles();

                // begin the upload
                string url = serviceURLBase + this.appKey + "/uploads";
                LogMessage("Uploading to: " + url);

                HttpWebRequest myRequest = (HttpWebRequest)WebRequest.Create(url);
                myRequest.Method = "POST";
                myRequest.ContentType = "application/json";
                myRequest.BeginGetRequestStream(HttpRequestCallback, myRequest);
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }
        }

        private static async void HttpRequestCallback(IAsyncResult asynchronousResult)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)asynchronousResult.AsyncState;
                using (Stream postStream = request.EndGetRequestStream(asynchronousResult))
                {

                    String contents = await GetUploadContents();
                    byte[] byteArray = Encoding.UTF8.GetBytes(contents);
                    postStream.Write(byteArray, 0, byteArray.Length);
                }

                request.BeginGetResponse(GetResponseCallback, request);
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }
        }

        private static async void GetResponseCallback(IAsyncResult asynchronousResult)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)asynchronousResult.AsyncState;
                using (HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(asynchronousResult))
                {
                    using (Stream streamResponse = response.GetResponseStream())
                    {
                        using (StreamReader streamRead = new StreamReader(streamResponse))
                        {
                            string responseString = streamRead.ReadToEnd();

                            LogMessage("Upload complete. Response: " + responseString);
                            await DeleteUploadFiles();
                        }
                    }
                }
            }
            catch (WebException e)
            {
                Debug.WriteLine("WebException raised.");
                Debug.WriteLine("\n{0}", e.Message);
                Debug.WriteLine("\n{0}", e.Status);
            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception raised!");
                Debug.WriteLine("Message : " + e.Message);
            }
            finally
            {
                isUploading = false;
            }
        }
        #endregion

        #region Data Lookups

        /// <summary>
        /// Gets the sequence number for the next upload blob. 
        /// </summary>
        /// <returns>Sequence number as a string</returns>
        private static async Task<string> GetSequenceNumber()
        {
            // open the meta file and read the next sequence number.
            var store = getStore();
            string metaFile = directoryName + @"\" + metaFileName;
            var file = await FileExists(store, metaFile);
            if (file == null || await IsFileEmpty(file))
            {
                await SetNextSequenceNumber("1");
                return "1";
            }
            string sequenceNumber;
            using (var stream = await file.OpenStreamForReadAsync())
            {
                using (TextReader reader = new StreamReader(stream))
                {
                    string installID = reader.ReadLine();
                    sequenceNumber = reader.ReadLine();
                }
            }
            return sequenceNumber;
        }

        /// <summary>
        /// Sets the next sequence number in the metadata file. Creates the file if its not already there
        /// </summary>
        /// <param name="number">Next sequence number</param>
        private static async Task SetNextSequenceNumber(string number)
        {
            var store = getStore();
            string metaFile = directoryName + @"\" + metaFileName;
            var file = await FileExists(store, metaFile);
            if (file == null || await IsFileEmpty(file))
            {
                // Create a new metadata file consisting of a unique installation ID and a sequence number
                await AppendTextToFile(Guid.NewGuid().ToString() + Environment.NewLine + number, metaFileName);
            }
            else
            {
                string installId;
                using (var filestream = await file.OpenStreamForReadAsync())
                {
                    using (TextReader reader = new StreamReader(filestream))
                    {
                        installId = reader.ReadLine();
                    }
                }

                // overwite the file w/ the old install ID and the new sequence number
                using (var fileOut = await store.OpenStreamForWriteAsync(metaFile, CreationCollisionOption.ReplaceExisting))
                {
                    using (TextWriter writer = new StreamWriter(fileOut))
                    {
                        writer.WriteLine(installId);
                        writer.Write(number);
                        writer.Flush();
                    }
                }
            }
        }

        private static async Task<StorageFile> FileExists(StorageFolder folder, string path)
        {
            try
            {
                return await folder.GetFileAsync(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static async Task<bool> IsFileEmpty(StorageFile file)
        {
            using (var stream = await file.OpenStreamForReadAsync())
            {
                return stream.Length == 0;
            }
        }

        /// <summary>
        /// Gets the timestamp of the storage file containing the sequence numbers. This allows processing to
        /// ignore duplicates or identify missing uploads
        /// </summary>
        /// <returns>A string containing a Unixtime</returns>
        private static async Task<string> GetPersistStoreCreateTime()
        {
            var store = getStore();
            string metaFile = directoryName + @"\" + metaFileName;
            var file = await FileExists(store, metaFile);
            DateTimeOffset dto;
            if (file == null || await IsFileEmpty(file))
            {
                await SetNextSequenceNumber("1");
                dto = DateTimeOffset.MinValue;
            }
            else
            {
                dto = file.DateCreated;
            }
            int secondsSinceUnixEpoch = (int)Math.Round((dto.DateTime - new DateTime(1970, 1, 1).ToLocalTime()).TotalSeconds);
            return secondsSinceUnixEpoch.ToString();
        }

        /// <summary>
        /// Gets the Installation ID out of the metadata file
        /// </summary>
        private static async Task<string> GetInstallId()
        {
            var store = getStore();
            using (var file = await store.OpenStreamForReadAsync(directoryName + @"\" + metaFileName))
            {
                using (TextReader reader = new StreamReader(file))
                {
                    return reader.ReadLine();
                }
            }
        }

        private static string _version;
        /// <summary>
        /// Retreives the Application Version from the metadata
        /// </summary>
        /// <returns>User generated app version</returns>
        public static string GetAppVersion()
        {
            if (string.IsNullOrEmpty(_version))
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                _version = string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
            }
            return _version;
        }

        /// <summary>
        /// Gets the current date/time as a string which can be inserted in the DB
        /// </summary>
        /// <returns>A formatted string with date, time, and timezone information</returns>
        private static string GetDatestring()
        {
            return DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'");
        }

        /// <summary>
        /// Gets the current time in unixtime
        /// </summary>
        /// <returns>The current time in unixtime</returns>
        private static double GetTimeInUnixTime()
        {
            return Math.Round(((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds), 0);
        }
        #endregion

        /// <summary>
        /// Constructs a blob header for uploading
        /// </summary>
        /// <returns>A string containing a blob header</returns>
        private async Task<string> GetBlobHeader()
        {
            StringBuilder blobString = new StringBuilder();

            //{ "dt":"h",  // data type, h for header
            //  "pa": int, // persistent store created at
            //  "seq": int,  // blob sequence number, incremented on each new blob, 
            //               // remembered in the persistent store
            //  "u": string, // A unique ID for the blob. Must be the same if the blob is re-uploaded!
            //  "attrs": {
            //    "dt": "a" // data type, a for attributes
            //    "au":string // Localytics Application Id
            //    "du":string // Device UUID
            //    "s":boolean // Whether the app has been stolen (optional)
            //    "j":boolean // Whether the device has been jailbroken (optional)
            //    "lv":string // Library version
            //    "av":string // Application version
            //    "dp":string // Device Platform
            //    "dll":string // Locale Language (optional)
            //    "dlc":string // Locale Country (optional)
            //    "nc":string // Network Country (iso code) (optional)
            //    "dc":string // Device Country (iso code) (optional)
            //    "dma":string // Device Manufacturer (optional)
            //    "dmo":string // Device Model
            //    "dov":string // Device OS Version
            //    "nca":string // Network Carrier (optional)
            //    "dac":string // Data Connection Type (optional)
            //    "mnc":int // mobile network code (optional)
            //    "mcc":int // mobile country code (optional)
            //    "tdid":string // Telephony Device Id (meid or imei) (optional)
            //    "wmac":string // hashed wifi mac address (optional)
            //    "emac":string // hashed ethernet mac address (optional)
            //    "bmac":string // hashed bluetooth mac address (optional)
            //    "iu":string // install id
            //    "udid":string } } // client side hashed version of the udid
            blobString.Append("{\"dt\":\"h\",");
            blobString.Append("\"pa\":" + await GetPersistStoreCreateTime() + ",");

            string sequenceNumber = await GetSequenceNumber();
            blobString.Append("\"seq\":" + sequenceNumber + ",");
            await SetNextSequenceNumber((int.Parse(sequenceNumber) + 1).ToString());

            blobString.Append("\"u\":\"" + Guid.NewGuid().ToString() + "\",");
            blobString.Append("\"attrs\":");
            blobString.Append("{\"dt\":\"a\",");
            blobString.Append("\"au\":\"" + this.appKey + "\",");
            blobString.Append("\"du\":\"" + GetDeviceInfo() + "\",");
            blobString.Append("\"lv\":\"" + libraryVersion + "\",");
            blobString.Append("\"av\":\"" + GetAppVersion() + "\",");
            blobString.Append("\"dp\":\"Windows 8\",");
            blobString.Append("\"dll\":\"" + CultureInfo.CurrentCulture.TwoLetterISOLanguageName + "\",");
            // there's no way to get the device model info, so we'll call it a computer
            blobString.Append("\"dmo\":\"" + "Computer" + "\",");
            // there's no way to get the current OS version, so we'll go with the compiled processor architecture
            blobString.Append("\"dov\":\"" + Windows.ApplicationModel.Package.Current.Id.Architecture.ToString() + "\",");
            blobString.Append("\"iu\":\"" + await GetInstallId() + "\"");
            blobString.Append("}}");
            blobString.Append(Environment.NewLine);

            return blobString.ToString();
        }

        private static string GetDeviceInfo()
        {
            var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha512).CreateHash();
            hash.Append(HardwareIdentification.GetPackageSpecificToken(null).Id);
            return CryptographicBuffer.EncodeToHexString(hash.GetValueAndReset());
        }

        /// <summary>
        /// Formats an input string for YAML
        /// </summary>       
        /// <returns>string sorrounded in quotes, with dangerous characters escaped</returns>
        private static string EscapeString(string input)
        {
            string escapedSlahes = input.Replace("\\", "\\\\");
            return "\"" + escapedSlahes.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Outputs a message to the debug console
        /// </summary>
        private static void LogMessage(string msg)
        {
            Debug.WriteLine("(localytics) " + msg);
        }
        #endregion

        #region public methods
        /// <summary>
        /// Creates a Localytics Session object
        /// </summary>
        /// <param name="appKey"> The key unique for each application generated at www.localytics.com</param>
        public LocalyticsSession(string appKey)
        {
            this.appKey = appKey;

            // Store the time and sequence number 
        }

        /// <summary>
        /// Opens or resumes the Localytics session.
        /// </summary>
        public async Task Open()
        {
            if (this.isSessionOpen || this.isSessionClosed)
            {
                LogMessage("Session is already opened or closed.");
                return;
            }

            try
            {
                if (await GetNumberOfStoredSessions() > maxStoredSessions)
                {
                    LogMessage("Local stored session count exceeded.");
                    return;
                }

                this.sessionUuid = Guid.NewGuid().ToString();
                this.sessionFilename = sessionFilePrefix + this.sessionUuid;
                this.sessionStartTime = GetTimeInUnixTime();

                // Format of an open session:
                //{ "dt":"s",       // This is a session blob
                //  "ct": long,     // seconds since Unix epoch
                //  "u": string     // A unique ID attached to this session 
                //  "nth": int,     // This is the nth session on the device. (not required)
                //  "new": boolean, // New vs returning (not required)
                //  "sl": long,     // seconds since last session (not required)
                //  "lat": double,  // latitude (not required)
                //  "lng": double,  // longitude (not required)
                //  "c0" : string,  // custom dimensions (not required)
                //  "c1" : string,
                //  "c2" : string,
                //  "c3" : string }

                StringBuilder openstring = new StringBuilder();
                openstring.Append("{\"dt\":\"s\",");
                openstring.Append("\"ct\":" + GetTimeInUnixTime().ToString() + ",");
                openstring.Append("\"u\":\"" + this.sessionUuid + "\"");
                openstring.Append("}");
                openstring.Append(Environment.NewLine);

                await AppendTextToFile(openstring.ToString(), this.sessionFilename);

                this.isSessionOpen = true;
                LogMessage("Session opened.");
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }
        }

        /// <summary>
        /// Closes the Localytics session.
        /// </summary>
        public async Task Close()
        {
            if (this.isSessionOpen == false || this.isSessionClosed == true)
            {
                LogMessage("Session not closed b/c it is either not open or already closed.");
                return;
            }

            try
            {
                //{ "dt":"c", // close data type
                //  "u":"abec86047d-ae51", // unique id for teh close
                //  "ss": session_start_time, // time the session was started
                //  "su":"696c44ebf6f",   // session uuid
                //  "ct":1302559195,  // client time
                //  "ctl":114,  // session length (optional)
                //  "cta":60, // active time length (optional)
                //  "fl":["1","2","3","4","5","6","7","8","9"], // Flows (optional)
                //  "lat": double,  // lat (optional)
                //  "lng": double,  // lng (optional)
                //  "c0" : string,  // custom dimensions (otpinal)
                //  "c1" : string,
                //  "c2" : string,
                //  "c3" : string }

                StringBuilder closeString = new StringBuilder();
                closeString.Append("{\"dt\":\"c\",");
                closeString.Append("\"u\":\"" + Guid.NewGuid().ToString() + "\",");
                closeString.Append("\"ss\":" + this.sessionStartTime.ToString() + ",");
                closeString.Append("\"su\":\"" + this.sessionUuid + "\",");
                closeString.Append("\"ct\":" + GetTimeInUnixTime().ToString());
                closeString.Append("}");
                closeString.Append(Environment.NewLine);
                await AppendTextToFile(closeString.ToString(), this.sessionFilename); // the close blob

                this.isSessionOpen = false;
                this.isSessionClosed = true;
                LogMessage("Session closed.");
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }
        }

        /// <summary>
        /// Creates a new thread which collects any files and uploads them. Returns immediately if an upload
        /// is already happenning.
        /// </summary>
        public async Task Upload()
        {
            if (isUploading)
            {
                return;
            }

            isUploading = true;

            try
            {
                // Do all the upload work on a seperate thread.
                await Task.Run((Func<Task>)BeginUpload);
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }
        }

        /// <summary>
        /// Records a specific event as having occured and optionally records some attributes associated with this event.
        /// This should not be called inside a loop. It should not be used to record personally identifiable information
        /// and it is best to define all your event names rather than generate them programatically.
        /// </summary>
        /// <param name="eventName">The name of the event which occured. E.G. 'button pressed'</param>
        /// <param name="attributes">Key value pairs that record data relevant to the event.</param>
        public async Task TagEvent(string eventName, Dictionary<string, string> attributes = null)
        {
            if (this.isSessionOpen == false)
            {
                LogMessage("Event not tagged because session is not open.");
                return;
            }

            //{ "dt":"e",  // event data time
            //  "ct":1302559181,   // client time
            //  "u":"48afd8beebd3",   // unique id
            //  "su":"696c44ebf6f",   // session id
            //  "n":"Button Clicked",  // event name
            //  "lat": double,   // lat (optional)
            //  "lng": double,   // lng (optional)
            //  "attrs":   // event attributes (optional)
            //  {
            //      "Button Type":"Round"
            //  },
            //  "c0" : string, // custom dimensions (optional)
            //  "c1" : string,
            //  "c2" : string,
            //  "c3" : string }

            try
            {
                StringBuilder eventString = new StringBuilder();
                eventString.Append("{\"dt\":\"e\",");
                eventString.Append("\"ct\":" + GetTimeInUnixTime().ToString() + ",");
                eventString.Append("\"u\":\"" + Guid.NewGuid().ToString() + "\",");
                eventString.Append("\"su\":\"" + this.sessionUuid + "\",");
                eventString.Append("\"n\":" + EscapeString(eventName));

                if (attributes != null)
                {
                    eventString.Append(",\"attrs\": {");
                    bool first = true;
                    foreach (string key in attributes.Keys)
                    {
                        if (!first) { eventString.Append(","); }
                        eventString.Append(EscapeString(key ?? string.Empty) + ":" + EscapeString(attributes[key] ?? string.Empty));
                        first = false;
                    }
                    eventString.Append("}");
                }
                eventString.Append("}");
                eventString.Append(Environment.NewLine);

                await AppendTextToFile(eventString.ToString(), this.sessionFilename); // the close blob
                LogMessage("Tagged event: " + EscapeString(eventName));
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }
        }
        #endregion
    }
}
