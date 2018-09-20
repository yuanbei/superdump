﻿using System;
using Microsoft.AspNetCore.Mvc;
using SuperDumpService.Services;
using System.Threading.Tasks;
using SuperDumpService.Models;
using SuperDumpService.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using SuperDumpService.Helpers;
using Hangfire;
using Microsoft.Extensions.Options;

namespace SuperDumpService.Controllers {
	[AutoValidateAntiforgeryToken]
	[Authorize(Policy = LdapCookieAuthenticationExtension.AdminPolicy)]
	public class AdminController : Controller {

		private readonly SimilarityService similarityService;
		private readonly DumpRepository dumpRepository;
		private readonly BundleRepository bundleRepository;
		private readonly IdenticalDumpRepository identicalDumpRepository;
		private readonly JiraIssueRepository jiraIssueRepository;
		private readonly ElasticSearchService elasticService;
		private readonly ILogger<AdminController> logger;
		private readonly SuperDumpSettings settings;

		public AdminController(SimilarityService similarityService,
				DumpRepository dumpRepository,
				BundleRepository bundleRepository,
				ILoggerFactory loggerFactory,
				IdenticalDumpRepository identicalDumpRepository, 
				JiraIssueRepository jiraIssueRepository, 
				IOptions<SuperDumpSettings> settings,
				ElasticSearchService elasticService) {
			this.similarityService = similarityService;
			this.dumpRepository = dumpRepository;
			this.bundleRepository = bundleRepository;
			this.identicalDumpRepository = identicalDumpRepository;
			this.jiraIssueRepository = jiraIssueRepository;
			this.elasticService = elasticService;
			logger = loggerFactory.CreateLogger<AdminController>();
			this.settings = settings.Value;
		}

		[HttpGet]
		public IActionResult Overview() {
			return View();
		}

		[HttpGet]
		public async Task<IActionResult> CompareDumps(string bundleId1, string dumpId1, string bundleId2, string dumpId2) {
			try {
				var res1 = await dumpRepository.GetResult(bundleId1, dumpId1);
				var res2 = await dumpRepository.GetResult(bundleId2, dumpId2);

				if (res1 == null || res2 == null) {
					return View(new SimilarityModel($"could not compare dumps."));
				}
				logger.LogSimilarityEvent("CompareDumps", HttpContext, bundleId1, dumpId1, bundleId2, dumpId2);
				var similarity = CrashSimilarity.Calculate(res1, res2);
				return View(new SimilarityModel(new DumpIdentifier(bundleId1, dumpId2), new DumpIdentifier(bundleId2, dumpId2), similarity));
			} catch (Exception e) {
				return View(new SimilarityModel($"exception while comparing: {e.ToString()}"));
			}
		}

		[HttpPost]
		public IActionResult TriggerSimilarityAnalysis(string bundleId, string dumpId) {
			BundleMetainfo bundleInfo = bundleRepository.Get(bundleId);
			if (bundleInfo == null) {
				logger.LogNotFound("TriggerSimilarityAnalysis: Bundle not found", HttpContext, "BundleId", bundleId);
				return View(null);
			}
			logger.LogDumpAccess("TriggerSimilarityAnalysis", HttpContext, bundleInfo, dumpId);
			similarityService.ScheduleSimilarityAnalysis(new DumpIdentifier(bundleId, dumpId), true, DateTime.MinValue);
			return RedirectToAction("Report", "Home", new { bundleId = bundleId, dumpId = dumpId }); // View("/Home/Report", new ReportViewModel(bundleId, dumpId));
		}

		[HttpPost]
		public IActionResult TriggerSimilarityAnalysisForAllDumps(bool force, int days = -1) {
			logger.LogAdminEvent("TriggerSimilarityAnalysisForAllDumps", HttpContext);
			DateTime timeFrom = DateTime.MinValue;
			if (days > 0) {
				timeFrom = DateTime.Now - TimeSpan.FromDays(days);
			}
			similarityService.TriggerSimilarityAnalysisForAllDumps(force, timeFrom);
			return View("Overview");
		}

		[HttpPost]
		public async Task<IActionResult> WipeAll() {
			logger.LogAdminEvent("SimilarityWipeAll", HttpContext);
			await similarityService.WipeAll();
			return View("Overview");
		}

		[HttpPost]
		public IActionResult CleanSimilarityAnalysisQueue() {
			logger.LogAdminEvent("CleanSimilarityAnalysisQueue", HttpContext);
			similarityService.CleanQueue();
			return View("Overview");
		}

		[HttpPost]
		public async Task<IActionResult> CreateAllIdenticalDumpRelationships() {
			logger.LogAdminEvent("CreateAllIdenticalDumpRelationships", HttpContext);
			await identicalDumpRepository.CreateAllIdenticalRelationships();
			return View("Overview");
		}

		[HttpPost]
		public async Task<IActionResult> WipeAllIdenticalDumpRelationships() {
			logger.LogAdminEvent("WipeAllIdenticalDumpRelationships", HttpContext);
			await identicalDumpRepository.WipeAll();
			return View("Overview");
		}

		[HttpPost]
		public IActionResult ForceRefreshAllJiraIssues() {
			if (settings.UseJiraIntegration) {
				logger.LogAdminEvent("ForceRefreshAllJiraIssues", HttpContext);
				BackgroundJob.Enqueue(() => jiraIssueRepository.ForceRefreshAllIssuesAsync());
				return View("Overview");
			}
			return NotFound();
		}

		[HttpPost]
		public async Task<IActionResult> WipeJiraIssueCache() {
			if (settings.UseJiraIntegration) {
				logger.LogAdminEvent("WipeJiraIssueCache", HttpContext);
				await jiraIssueRepository.WipeJiraIssueCache();
				return View("Overview");
			}
			return NotFound();
		}

		[HttpPost]
		public IActionResult ForceSearchBundleIssues(int days = -1) {
			if (settings.UseJiraIntegration) {
				logger.LogAdminEvent("ForceSearchBundleIssues", HttpContext);
				TimeSpan timeFrom = days > 0 ? TimeSpan.FromDays(days) : TimeSpan.MaxValue;
				BackgroundJob.Enqueue(() => jiraIssueRepository.SearchAllBundleIssues(timeFrom, true));
				return View("Overview");
			}
			return NotFound();
		}

		[HttpPost]
		public IActionResult PushElasticSearch(bool clean) {
			logger.LogElasticClean("PushElasticSearch", HttpContext, clean);
			BackgroundJob.Enqueue(() => elasticService.PushAllResultsAsync(clean));
			return View("Overview");
		}
	}
}