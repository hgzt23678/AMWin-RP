﻿using System;
using System.Diagnostics;
using System.Timers;
using System.Windows.Automation;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AMWin_RichPresence {

    internal class AppleMusicInfo {
        // the following fields are only valid if HasSong is true.
        // DateTimes are in UTC.
        public string        SongName;
        public string        SongSubTitle;
        public string        SongAlbum;
        public string        SongArtist;
        public bool          IsPaused = true;
        public DateTime?     PlaybackStart;
        public DateTime?     PlaybackEnd;
        public int?          SongDuration = null;
        public List<string>? ArtistList = null;
        public string?       CoverArtUrl = null;
        public int?          CurrentTime = null;

        public AppleMusicInfo(string songName, string songSubTitle, string songAlbum, string songArtist) {
            this.SongName = songName;
            this.SongSubTitle = songSubTitle;
            this.SongAlbum = songAlbum;
            this.SongArtist = songArtist;
        }

        public override string ToString() {
            return $"""
                [AppleMusicInfo] 
                _________________________________________________
                |  {SongName}
                |  by {SongArtist} on {SongAlbum},
                |------------------------------------------------
                |  Cover Art URL: {CoverArtUrl},
                -------------------------------------------------
                """;
        }
        public void Print() {
            Trace.WriteLine(ToString());
        }
    }

    internal class AppleMusicClientScraper {

        public delegate void RefreshHandler(AppleMusicInfo? newInfo);
        string lastFmApiKey;
        Timer timer;
        RefreshHandler refreshHandler;
        AppleMusicInfo? currentSong;
        public bool composerAsArtist; // for classical music, treat composer (not performer) as artist

        public AppleMusicClientScraper(string lastFmApiKey, int refreshPeriodInSec, bool composerAsArtist, RefreshHandler refreshHandler) {
            this.refreshHandler = refreshHandler;
            this.lastFmApiKey = lastFmApiKey;
            timer = new Timer(refreshPeriodInSec * 1000);
            timer.Elapsed += Refresh;
            Refresh(this, null);
            timer.Start();
            this.composerAsArtist = composerAsArtist;
        }

        public void Refresh(object? source, ElapsedEventArgs? e) {
            AppleMusicInfo? appleMusicInfo = null;
            AutomationElement? appleMusicWindow;

            appleMusicWindow = FindAppleMusicWindow();
            try {
                if (appleMusicWindow != null) {
                    appleMusicInfo = GetAppleMusicInfo(appleMusicWindow);
                }
            } catch (Exception ex) {
                Trace.WriteLine($"Something went wrong while scraping: {ex}");
            }
            refreshHandler(appleMusicInfo);
        }
        public static AutomationElement? FindAppleMusicWindow() {
            try {
                var allWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement element in allWindows) {
                    var elementProperties = element.Current;
                    // TODO - How do we tell it's the actual Windows-native Apple Music application and not some other one?
                    if (elementProperties.Name == "Apple Music" && elementProperties.ClassName == "WinUIDesktopWin32WindowClass") {
                        return element;
                    }
                }
            } catch (ElementNotAvailableException) {
                return null;
            }
            return null;
        }

        public AppleMusicInfo? GetAppleMusicInfo(AutomationElement amWindow) {

            var amWinTransportBar = amWindow
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Microsoft.UI.Content.DesktopChildSiteBridge"))
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "NavView"))
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "TransportBar"));

            var amWinLCD = amWinTransportBar
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "LCD"));

            var songFields = amWinLCD
                .FindAll  (TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "myScrollViewer"));

            // ================================================
            //  Check if there is a song playing
            // ------------------------------------------------

            if (songFields.Count != 2) {
                return null;
            }

            // ================================================
            //  Get song info
            // ------------------------------------------------

            var songNameElement = songFields[0];
            var songAlbumArtistElement = songFields[1];

            // the upper rectangle is the song name; the bottom rectangle is the author/album
            // lower .Bottom = higher up on the screen (?)
            if (songNameElement.Current.BoundingRectangle.Bottom > songAlbumArtistElement.Current.BoundingRectangle.Bottom) {
                songNameElement = songFields[1];
                songAlbumArtistElement = songFields[0];
            }

            var songName = songNameElement.Current.Name;
            var songAlbumArtist = songAlbumArtistElement.Current.Name;

            string songArtist;
            string songAlbum;

            // some classical songs add "By " before the composer's name
            string songComposer = null;
            string songPerformer = null; 
            var composerPerformerRegex = new Regex(@"By\s.*?\s\u2014");
            var songComposerPerformer = composerPerformerRegex.Matches(songAlbumArtist);
            try {
                if (songComposerPerformer.Count > 0) {
                    songComposer = songAlbumArtist.Split(" \u2014 ")[0].Remove(0, 3);
                    songPerformer = songAlbumArtist.Split(" \u2014 ")[1];
                    songArtist = composerAsArtist ? songComposer : songPerformer;
                    songAlbum = songAlbumArtist.Split(" \u2014 ")[2];
                } else {
                    // U+2014 is the emdash used by the Apple Music app, not the standard "-" character on the keyboard!
                    songArtist = songAlbumArtist.Split(" \u2014 ")[0];
                    songAlbum = songAlbumArtist.Split(" \u2014 ")[1];
                }
            } catch {
                Trace.WriteLine($"Could not parse '{songAlbumArtist}' into artist and album.");
                songArtist = "";
                songAlbum = "";
            }

            // when searching for song info, use the performer as the artist instead of composer
            string songSearchArtist = songPerformer ?? songArtist;

            // if this is a new song, clear out the current song
            if (currentSong == null || currentSong?.SongName != songName || currentSong?.SongArtist != songArtist || currentSong?.SongSubTitle != songAlbumArtist) {
                currentSong = new AppleMusicInfo(songName, songAlbumArtist, songAlbum, songArtist);
            }
            
            // find artist list... unless it's a classical song
            if (currentSong.ArtistList == null && songComposer == null) {
                AppleMusicWebScraper.GetArtistList(songName, songAlbum, songSearchArtist).ContinueWith(t => {
                    currentSong.ArtistList = t.Result;
                    if (currentSong.ArtistList.Count == 0) {
                        currentSong.ArtistList = null;
                    }
                });
            }
            // ================================================
            //  Get song timestamps
            // ------------------------------------------------

            var currentTimeElement       = amWinLCD.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "CurrentTime"));
            var remainingDurationElement = amWinLCD.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "Duration"));

            // grab the seek slider to check song playback progress
            var songProgressSlider = amWinLCD
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "LCDScrubber"))? // this may be hidden when a song is initialising
                .GetCurrentPattern(RangeValuePattern.Pattern) as RangeValuePattern;

            var songProgressPercent = songProgressSlider == null ? 0 : songProgressSlider.Current.Value / songProgressSlider.Current.Maximum;

            // calculate song timestamps
            int? currentTime = null;
            int? remainingDuration = null;

            // if the timestamps are being hidden by Apple Music, we fall back to independent timestamp calculation
            if (currentTimeElement == null || remainingDurationElement == null) {

                // try to get song duration if we don't have it
                if (currentSong.SongDuration == null) {
                    AppleMusicWebScraper.GetSongDuration(lastFmApiKey, songName, songAlbum, songSearchArtist).ContinueWith(t => {
                        string? dur = t.Result;
                        currentSong.SongDuration = dur == null ? null : ParseTimeString(dur);
                    });
                }

                // if success, set timestamps
                if (currentSong.SongDuration != null) {
                    var songDuration = currentSong.SongDuration;
                    currentTime = (int)(songProgressPercent * songDuration);
                    remainingDuration = (int)((1 - songProgressPercent) * songDuration);
                }

            } else { // ... otherwise just use the timestamps provided by Apple Music
                currentTime = ParseTimeString(currentTimeElement!.Current.Name);
                remainingDuration = ParseTimeString(remainingDurationElement!.Current.Name);
            }

            currentSong.CurrentTime = currentTime;

            // if we have timestamps, convert them into Unix timestamps for Discord
            if (currentTime != null && remainingDuration != null) {
                currentSong.PlaybackStart = DateTime.UtcNow - new TimeSpan(0, 0, (int)currentTime);
                currentSong.PlaybackEnd = DateTime.UtcNow + new TimeSpan(0, 0, (int)remainingDuration);
            }

            // check if the song is paused or not
            var playPauseButton = amWinTransportBar
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "TransportControl_PlayPauseStop"));

            currentSong.IsPaused = playPauseButton.Current.Name == "Play";


            // ================================================
            //  Get song cover art
            // ------------------------------------------------

            if (currentSong.CoverArtUrl == null) {
                AppleMusicWebScraper.GetAlbumArtUrl(lastFmApiKey, songName, songAlbum, songSearchArtist).ContinueWith(t => {
                    currentSong.CoverArtUrl = t.Result;
                });
            }

            return currentSong;
        }

        // e.g. parse "-1:30" to 90 seconds
        private static int ParseTimeString(string time) {

            // remove leading "-"
            if (time.Contains('-')) {
                time = time.Split('-')[1];
            }

            int min = int.Parse(time.Split(":")[0]);
            int sec = int.Parse(time.Split(":")[1]);

            return min * 60 + sec;
        }
    }
}
