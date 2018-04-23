﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Globalization;
using System.Threading;
using Timer = System.Threading.Timer;
using GitHub.Logging;

namespace GitHub.Unity
{
    class UsageTracker : IUsageTracker
    {
        private static ILogging Logger { get; } = LogHelper.GetLogger<UsageTracker>();

        private readonly NPath storePath;
        private readonly ISettings userSettings;
        private readonly IMetricsService metricsService;
        private readonly string guid;
        private readonly string unityVersion;
        private Timer timer;

        public UsageTracker(IMetricsService metricsService, ISettings userSettings, NPath storePath, string guid, string unityVersion)
        {
            this.userSettings = userSettings;
            this.metricsService = metricsService;
            this.guid = guid;
            this.storePath = storePath;
            this.unityVersion = unityVersion;

            Logger.Trace("guid:{0}", guid);
            if (Enabled)
                RunTimer(3*60);
        }

        private UsageStore LoadUsage()
        {
            UsageStore result = null;
            string json = null;
            if (storePath.FileExists())
            {
                Logger.Trace("LoadUsage: \"{0}\"", storePath);

                try
                {
                    json = storePath.ReadAllText(Encoding.UTF8);
                    if (json != null)
                    {
                        result = SimpleJson.DeserializeObject<UsageStore>(json);

                        if (result.Model.Reports.Any(usage => usage.Dimensions.Date == DateTime.MinValue))
                        {
                            Logger.Trace("Legacy usage data migration");
                            
                            var legacyData = SimpleJson.DeserializeObject(json) as JsonObject;
                            var model = legacyData?["Model"] as JsonObject;
                            var reports = model?["Reports"] as JsonArray;
                            if (reports != null)
                            {
                                result.Model.Reports = reports.Cast<JsonObject>().Select(report => {
                                    var usage = new Usage {
                                        Dimensions = {
                                            Guid = (string)report["Guid"],
                                            AppVersion = (string)report["AppVersion"],
                                            UnityVersion = (string)report["UnityVersion"],
                                            Lang = (string)report["Lang"],
                                            Date = DateTime.Parse((string)report["Date"]).ToUniversalTime()
                                        },
                                        Measures = {
                                            NumberOfStartups = int.Parse(report["NumberOfStartups"].ToString())
                                        }
                                    };
                                    return usage;
                                }).ToList();
                            }
                            else
                            {
                                throw new InvalidOperationException("Error migrating usage data");
                            }

                            SaveUsage(result);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Error Loading Usage: {0}; Deleting File", storePath);

                    try
                    {
                        storePath.DeleteIfExists();
                    }
                    catch {}
                }
            }

            if (result == null)
                result = new UsageStore();

            if (String.IsNullOrEmpty(result.Model.Guid))
                result.Model.Guid = guid;

            return result;
        }

        private void SaveUsage(UsageStore store)
        {
            var pathString = storePath.ToString();
            Logger.Trace("SaveUsage: \"{0}\"", pathString);

            try
            {
                var json = SimpleJson.SerializeObject(store);
                storePath.WriteAllText(json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SaveUsage Error: \"{0}\"", pathString);
            }
        }

        private void RunTimer(int seconds)
        {
            Logger.Trace($"Scheduling timer for {seconds} seconds from now");
            timer = new Timer(async _ =>
            {
                try
                {
                    timer.Dispose();
                    await SendUsage();
                }
                catch {}
            }, null, seconds * 1000, Timeout.Infinite);
        }

        private async Task SendUsage()
        {
            var usageStore = LoadUsage();

            if (metricsService == null)
            {
                Logger.Warning("No service, not sending usage");
                return;
            }

            if (usageStore.LastUpdated.Date != DateTimeOffset.UtcNow.Date)
            {
                Logger.Trace("Sending Usage");

                var currentTimeOffset = DateTimeOffset.UtcNow;
                var beforeDate = currentTimeOffset.Date;

                var success = false;
                var extractReports = usageStore.Model.SelectReports(beforeDate);
                if (!extractReports.Any())
                {
                    Logger.Trace("No items to send");
                }
                else
                {
                    if (!Enabled)
                    {
                        Logger.Trace("Metrics disabled");
                        return;
                    }

                    try
                    {
                        await metricsService.PostUsage(extractReports);
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(@"Error Sending Usage Exception Type:""{0}"" Message:""{1}""", ex.GetType().ToString(), ex.Message);
                    }
                }

                if (success)
                {
                    usageStore.Model.RemoveReports(beforeDate);
                    usageStore.LastUpdated = currentTimeOffset;
                    SaveUsage(usageStore);
                }
            }
        }

        private Usage GetCurrentUsage(UsageStore usageStore)
        {
            var usage = usageStore.Model.GetCurrentUsage(ApplicationConfiguration.AssemblyName.Version.ToString(), unityVersion);
            usage.Dimensions.Lang = CultureInfo.InstalledUICulture.IetfLanguageTag;
            usage.Dimensions.CurrentLang = CultureInfo.CurrentCulture.IetfLanguageTag;
            return usage;
        }

        public void IncrementNumberOfStartups()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfStartups++;
            Logger.Trace("NumberOfStartups:{0} Date:{1}", usage.Measures.NumberOfStartups, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfCommits()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfCommits++;
            Logger.Trace("NumberOfCommits:{0} Date:{1}", usage.Measures.NumberOfCommits, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfFetches()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfFetches++;
            Logger.Trace("NumberOfFetches:{0} Date:{1}", usage.Measures.NumberOfFetches, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfPushes()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfPushes++;
            Logger.Trace("NumberOfPushes:{0} Date:{1}", usage.Measures.NumberOfPushes, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfProjectsInitialized()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfProjectsInitialized++;
            Logger.Trace("NumberOfProjectsInitialized:{0} Date:{1}", usage.Measures.NumberOfProjectsInitialized, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfLocalBranchCreations()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfLocalBranchCreations++;
            Logger.Trace("NumberOfLocalBranchCreations:{0} Date:{1}", usage.Measures.NumberOfLocalBranchCreations, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfLocalBranchDeletions()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfLocalBranchDeletion++;
            Logger.Trace("NumberOfLocalBranchDeletion:{0} Date:{1}", usage.Measures.NumberOfLocalBranchDeletion, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfLocalBranchCheckouts()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfLocalBranchCheckouts++;
            Logger.Trace("NumberOfLocalBranchCheckouts:{0} Date:{1}", usage.Measures.NumberOfLocalBranchCheckouts, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfRemoteBranchCheckouts()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfRemoteBranchCheckouts++;
            Logger.Trace("NumberOfRemoteBranchCheckouts:{0} Date:{1}", usage.Measures.NumberOfRemoteBranchCheckouts, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfPulls()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfPulls++;
            Logger.Trace("NumberOfPulls:{0} Date:{1}", usage.Measures.NumberOfPulls, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public void IncrementNumberOfAuthentications()
        {
            var usageStore = LoadUsage();
            var usage = GetCurrentUsage(usageStore);

            usage.Measures.NumberOfAuthentications++;
            Logger.Trace("NumberOfAuthentications:{0} Date:{1}", usage.Measures.NumberOfAuthentications, usage.Dimensions.Date);

            SaveUsage(usageStore);
        }

        public bool Enabled
        {
            get
            {
                return userSettings.Get(Constants.MetricsKey, true);
            }
            set
            {
                if (value == Enabled)
                    return;
                userSettings.Set(Constants.MetricsKey, value);
                if (value)
                {
                    RunTimer(5);
                }
                else
                {
                    timer.Dispose();
                    timer = null;
                }
            }
        }
    }
}
