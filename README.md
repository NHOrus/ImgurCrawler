# ImgurCrawler

CLI .NET App for mass-downloading images from http://www.imgur.com/

WARNING: A LOT of the content this app finds is NSFW, so use discretion!

App will automatically pause once the download folder has reached 1GB. It can be resumed using the "resume" command, and may also be manually paused using the "pause" command.

Startup Params:

-s STRING - Forces the application to begin on a given string

-c INTEGER - Changes the number of url characters the application should iterate over. The default is 5, and anything higher dramatically decreases the likelihood of finding any images.

-m INTEGER - In megabytes, the maximum size the download folder may reach before the app pauses itself. The default is 1024, or 1GB
