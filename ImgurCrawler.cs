using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.Drawing;
using System.Net;
using System.Text;

namespace ImgurCrawler {
    public static class ImgurCrawler {
        #region UTILITY DATA

        private static class CONSTANTS {
            // Most browsers do ~6 concurrent non-script downloads, so let's replicate that here
            public const int NUM_THREADS = 6;

            // Limit searches to characters allowed by IMGUR
            public static readonly char[] IMGUR_URL_CHARACTERS = {
                'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U','V', 'W', 'X', 'Y', 'Z',
                'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u','v', 'w', 'x', 'y', 'z',
                '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            };

            // Rather than building out from an empty string, we will fill the number of characters with the default character, and change the character to build the string
            public const char DEFAULT_CHAR = '-';
        }

        private class ThreadStartParams {
            // Each thread operates on a separate section of the possible set, and thus must have a unique start string
            private readonly string start_string_;

            // As each thread processes a separate _XXXXX train of characters, we need to record how many characters we should skip each time
            private readonly int every_n_characters_;

            public ThreadStartParams(string start_string, int every_n_characters) {
                this.start_string_ = start_string;
                this.every_n_characters_ = every_n_characters;
            }

            public string StartString {
                get {
                    return start_string_;
                }
            }

            public int EveryNCharacters {
                get {
                    return every_n_characters_;
                }
            }
        }

        #endregion

        #region CONCURRENT MEMEBERS

        // Used in all threads, this counter should use Interlocked.Increment(), which will allow for thread-safe reads + writes
        private static int download_counter_ = 0;

        // Used in all threads, this should be set using Interlocked.Exchange(), which allows for thread-safe read + writes
        private static int downloading_paused_ = 0;

        // Avoid wasting time by unnecessarily checking links that couldn't possibly exist
        // I've seen 5, 6, and 7 length strings, but the likelihood of finding an image decreases DRAMATICALLY on higher counts
        private static int num_search_characters_ = 5;

        // Avoid filling up their entire hard-drive with images by setting a maximum size for the output directory, in megabytes
        private static int auto_pause_filesize_ = 1024;

        // Optionally, users may choose what string they want to begin on, allowing them to pick up the app again later on in the same spot that they left off
        private static string start_string_ = "";

        #endregion

        #region PUBLIC STATIC METHODS

        public static int Main(string[] args) {
            parseStatupArgs(args);
            Console.WriteLine("Run from command line with /? to view startup args!");

            if (!Directory.Exists("Download"))
                Directory.CreateDirectory("Download");

            // each thread will process the full-depth of a single starting character
            // as we can force a start string, we need to find out where we are starting so each thread can process a unique string
            int start_character_index = 0;
            if (start_string_.Length > 0) {
                for (var i = 0; i < CONSTANTS.IMGUR_URL_CHARACTERS.Length; ++i) {
                    if (start_string_[0] == CONSTANTS.IMGUR_URL_CHARACTERS[i]) {
                        start_character_index = i;
                        break;
                    }
                }
            }

            // in order to easily support start strings, we must ensure there always is one
            if (start_string_.Length > num_search_characters_)
                start_string_ = start_string_.Substring(0, num_search_characters_);
            else {
                var num_add_chars = num_search_characters_ - start_string_.Length;
                for (var i = 0; i < num_add_chars; ++i)
                    start_string_ += CONSTANTS.DEFAULT_CHAR;
            }

            // if there aren't enough characters left due to the start string, cap the number of threads to that
            var num_threads = Math.Min(CONSTANTS.NUM_THREADS, CONSTANTS.IMGUR_URL_CHARACTERS.Length - start_character_index);

            Console.WriteLine("Starting at " + start_string_ + " with " + num_threads + " threads! Enter 'help' to view console commands!");

            // since Console.ReadLine() is blocking, we must perform the filesize monitor in another thread
            var filesize_monitor_thread = new Thread(threadFilesizeMonitor);
            filesize_monitor_thread.Start();

            var download_threads = new Thread[num_threads];
            for (var i = 0; i < num_threads; ++i) {
                var thread_string = string.Concat(CONSTANTS.IMGUR_URL_CHARACTERS[start_character_index + i]);
                if (start_string_.Length > 0) {
                    thread_string += start_string_.Substring(1); // skip first character, as this is taken into consideration by the start index
                }

                var thread_params = new ThreadStartParams(thread_string, num_threads);
                download_threads[i] = new Thread(threadDownload);
                download_threads[i].Start(thread_params);
            }

            while (true) {
                // Console.ReadLine() will block until a command is entered
                var command = Console.ReadLine();
                switch (command) {
                    case "pause": {
                        Interlocked.Exchange(ref downloading_paused_, 1);
                        Console.WriteLine("PAUSED! Enter 'resume' to continue downloading!");
                        break;
                    }

                    case "resume": {
                        Interlocked.Exchange(ref downloading_paused_, 0);
                        Console.WriteLine("RESUMED!");
                        break;
                    }

                    default: {
                        Console.WriteLine("pause - Pauses the application");
                        Console.WriteLine("resume - Resumes the application");
                        break;
                    }
                }
            }
        }

        #endregion

        #region PRIVATE STATIC METHODS

        private static void printStartupArgumentHelp() {
            Console.WriteLine("Args:");
            Console.WriteLine("/? - Help");
            Console.WriteLine("-s STRING - Forces the procedure to begin at a given string");
            Console.WriteLine("-m VALUE - In megabytes, the maximum download folder size before automatic pausing. Default is 1024");
            Console.WriteLine("-c VALUE - Number of characters in url string, default 5, likelihood of finding images decreases dramatically when higher than that");
        }

        private static void parseStatupArgs(string[] args) {
            for (var i = 0; i < args.Length; ++i) {
                switch (args[i]) {
                    case "/?": {
                        printStartupArgumentHelp();
                        Environment.Exit(0);
                        break;
                    }

                    case "-c": {
                        // make sure we have the value
                        if (args.Length < i + 2) {
                            printStartupArgumentHelp();
                            Environment.Exit(-1);
                            break;
                        }

                        int val = 0;
                        if (!Int32.TryParse(args[i + 1], out val)) {
                            printStartupArgumentHelp();
                            Environment.Exit(-1);
                            break;
                        }

                        num_search_characters_ = val;
                        if (num_search_characters_ < 4 || num_search_characters_ > 8)
                            Console.WriteLine("WARNING: " + num_search_characters_ + " IS LIKELY GOING TO RETURN NO RESULTS!!!");
                        break;
                    }

                    case "-m": {
                        // make sure we have the value
                        if (args.Length < i + 2) {
                            printStartupArgumentHelp();
                            Environment.Exit(-1);
                            break;
                        }

                        int val = 0;
                        if (!Int32.TryParse(args[i + 1], out val)) {
                            printStartupArgumentHelp();
                            Environment.Exit(-1);
                            break;
                        }

                        auto_pause_filesize_ = val;
                        break;
                    }

                    case "-s": {
                        // make sure we have the value
                        if (args.Length < i + 2) {
                            printStartupArgumentHelp();
                            Environment.Exit(-1);
                            break;
                        }

                        var start = args[i + 1];
                        if (!Regex.IsMatch(start, "^[a-zA-Z0-9]*$")) {
                            printStartupArgumentHelp();
                            Environment.Exit(-1);
                            break;
                        }

                        start_string_ = start;
                        break;
                    }
                }
            }
        }

        private static void threadFilesizeMonitor() {
            while (true) {
                Thread.Sleep(500);

                if (downloading_paused_ == 1) {
                    continue;
                }

                long directorySize = directoryFileSize(new DirectoryInfo("Download"));
                int megabytes = (int)(directorySize / 1024 / 1024);
                if (megabytes > auto_pause_filesize_) {
                    Console.WriteLine("Download directory has reached " + megabytes + "MB! Downloading has been paused, please clean the directory and use the 'resume' command!");
                    Interlocked.Exchange(ref downloading_paused_, 1);
                }
            }
        }

        private static long directoryFileSize(DirectoryInfo d) {
            long size = 0;
            // Add file sizes.
            FileInfo[] fis = d.GetFiles();
            foreach (FileInfo fi in fis) {
                size += fi.Length;
            }
            // Add subdirectory sizes.
            DirectoryInfo[] dis = d.GetDirectories();
            foreach (DirectoryInfo di in dis) {
                size += directoryFileSize(di);
            }
            return size;
        }

        private static void threadDownload(object param) {
            var start_params = (ThreadStartParams)param;

            // for performance reasons and to support pre-set strings, we will use the StringBuilder, which allows us character-level setting features
            var string_builder = new StringBuilder(start_params.StartString);

            // each thread is assigned a character to process at [0] to start, so that they can all process separate sections of the possible string set
            // we need to know which character this is so we can ensure the threads do not collide
            int start_character_index = 0;
            for (var i = 0; i < CONSTANTS.IMGUR_URL_CHARACTERS.Length; ++i) {
                if (start_params.StartString[0] == CONSTANTS.IMGUR_URL_CHARACTERS[i]) {
                    start_character_index = i;
                    break;
                }
            }

            // run once according to the startup parameters
            recursiveBuildImgurURL(new StringBuilder(start_params.StartString), num_search_characters_ - 1);

            // run with the default string, changing the first character according to how much of an offset we expect between each thread to avoid collisions
            while (true) {
                var character = start_character_index + start_params.EveryNCharacters;
                if (character + 1 > CONSTANTS.IMGUR_URL_CHARACTERS.Length)
                    break;

                string_builder = new StringBuilder();
                for (var i = 0; i < num_search_characters_; ++i)
                    string_builder.Append(CONSTANTS.DEFAULT_CHAR);
                string_builder[0] = CONSTANTS.IMGUR_URL_CHARACTERS[character];

                recursiveBuildImgurURL(string_builder, num_search_characters_ - 1);
            }
        }

        private static void recursiveBuildImgurURL(StringBuilder download_string, int remaining_characters) {
            if (remaining_characters <= 0) {
                downloadImage(download_string.ToString());
                return;
            }

            var character_index = num_search_characters_ - remaining_characters;
            var start_character = 0;
            if (download_string[character_index] != CONSTANTS.DEFAULT_CHAR) {
                for (var i = 0; i < CONSTANTS.IMGUR_URL_CHARACTERS.Length; ++i) {
                    if (download_string[character_index] == CONSTANTS.IMGUR_URL_CHARACTERS[i]) {
                        start_character = i;
                        break;
                    }
                }
            }

            for (var i = start_character; i < CONSTANTS.IMGUR_URL_CHARACTERS.Length; ++i) {
                download_string[character_index] = CONSTANTS.IMGUR_URL_CHARACTERS[i];
                recursiveBuildImgurURL(download_string, remaining_characters - 1);
            }

            download_string[character_index] = CONSTANTS.DEFAULT_CHAR;
        }

        private static void downloadImage(string image_path) {
            while (downloading_paused_ == 1) {
                Thread.Sleep(100);
            }

            try {
                var request = HttpWebRequest.CreateHttp("http://i.imgur.com/" + image_path + ".jpg");
                var response = (HttpWebResponse)request.GetResponse();

                var image = Image.FromStream(response.GetResponseStream());

                // skip the "image not found" image
                if (image.Height == 81 && image.Width == 161) {
                    return;
                }

                var counter = Interlocked.Increment(ref download_counter_);
                if ((counter % 25) == 0) {
                    Console.WriteLine("Done: " + counter);
                }

                image.Save("Download\\" + counter + "_" + image_path + ".jpg");
            } catch {
                Console.WriteLine("Exception!!!");
                return;
            }
        }

        #endregion
    }


}
