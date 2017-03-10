namespace Sitecore.Support.ContentTesting.Requests.Controllers.Optimization
{
  using Sitecore.ContentTesting;
  using Sitecore.ContentTesting.ContentSearch.Models;
  using Sitecore.ContentTesting.Data;
  using Sitecore.ContentTesting.Extensions;
  using Sitecore.ContentTesting.Helpers;
  using Sitecore.ContentTesting.Intelligence;
  using Sitecore.ContentTesting.Model.Data.Items;
  using Sitecore.ContentTesting.ViewModel;
  using Sitecore.Data;
  using Sitecore.Data.Items;
  using Sitecore.Diagnostics;
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Web.Http;
  using System.Web.Http.Results;

  public class SupportTestsController : Sitecore.ContentTesting.Requests.Controllers.Optimization.TestsController
  {
    // copy of base value
    private readonly IContentTestStore _contentTestStore;

    public SupportTestsController() 
    {
      // copied from base class implementation
      _contentTestStore = ContentTestingFactory.Instance.ContentTestStore;
    }

    public SupportTestsController(IContentTestStore contentTestStore) : base(contentTestStore)
    {
      _contentTestStore = contentTestStore;
    }

    [HttpGet]
    public JsonResult<TestListViewModel> GetActiveTestsEx(int? page = new int?(), int? pageSize = new int?(), string hostItemId = null, string searchText = null)
    {
      page = new int?(page.HasValue ? page.GetValueOrDefault() : 1);
      pageSize = new int?(pageSize.HasValue ? pageSize.GetValueOrDefault() : 20);

      DataUri hostItemDataUri = null;
      if (!string.IsNullOrEmpty(hostItemId))
      {
        hostItemDataUri = DataUriParser.Parse(hostItemId, "");
      }

      var activeTests = ContentTestStore.GetActiveTests(hostItemDataUri, searchText, null)
        .ToArray();

      var items = new List<ExecutedTestViewModel>();
      var dictionary = new Dictionary<ID, ITestConfiguration>();

      foreach (var activeTest in activeTests)
      {
        Item item2 = Database.GetItem(activeTest.Uri);
        if (item2 != null)
        {
          var testDefinitionItem = TestDefinitionItem.Create(item2);
          if (testDefinitionItem != null)
          {
            var hostItem = (activeTest.HostItemUri != null) ? item2.Database.GetItem(activeTest.HostItemUri) : null;
            if (hostItem != null)
            {
              var testConfiguration = _contentTestStore.LoadTestForItem(hostItem, testDefinitionItem);
              if (testConfiguration != null)
              {
                var testId = testConfiguration.TestDefinitionItem.ID;
                var debugText = $"{testId}{testConfiguration.TestDefinitionItem?.InnerItem?.Paths.FullPath}, " +
                  $"TestSet.Id: {testConfiguration.TestSet?.Id}, " +
                  $"ContentItem: {testConfiguration.ContentItem?.ID}{testConfiguration.ContentItem?.Paths.FullPath}, " +
                  $"Language: {testConfiguration.LanguageName}, " +
                  $"TestSet.Name: {testConfiguration.TestSet?.Name}, " +
                  $"TestType: {testConfiguration.TestType}, " +
                  $"Variables.Count: {testConfiguration.Variables?.Count()}";

                try
                {
                  Log.Debug($"SupportTestsController: Adding {debugText}...", this);
                  dictionary.Add(testId, testConfiguration);
                }
                catch (Exception ex)
                {
                  throw new InvalidOperationException($"Failed to add {debugText}", ex);
                }

                var model = new ExecutedTestViewModel
                {
                  HostPageId = hostItem.ID.ToString(),
                  HostPageUri = hostItem.Uri.ToDataUri(),
                  HostPageName = hostItem.DisplayName,
                  DeviceId = testConfiguration.DeviceId.ToString(),
                  DeviceName = testConfiguration.DeviceName,
                  Language = testConfiguration.LanguageName,
                  CreatedBy = FormattingHelper.GetFriendlyUserName(item2.Security.GetOwner()),
                  Date = DateUtil.ToServerTime(testDefinitionItem.StartDate).ToString("dd-MMM-yyyy"),
                  ExperienceCount = testConfiguration.TestSet.GetExperienceCount(),
                  Days = GetEstimatedDurationDays(hostItem, testConfiguration.TestSet.GetExperienceCount(), testDefinitionItem),
                  ItemId = testDefinitionItem.ID.ToString(),
                  ContentOnly = testConfiguration.TestSet.Variables.Count == testDefinitionItem.PageLevelTestVariables.Count,
                  TestType = testConfiguration.TestType,
                  TestId = testId
                };

                items.Add(model);
              }
            }
          }
        }
      }
      
      items = items
        .OrderBy(x => x.Days)
        .Skip(((page.Value - 1) * pageSize.Value))
        .Take(pageSize.Value).ToList();

      foreach (ExecutedTestViewModel executedTest in items)
      {
        if (Database.GetItem(executedTest.HostPageUri) != null)
        {
          executedTest.Effect = GetWinningEffect(dictionary[executedTest.TestId]);
          if (executedTest.Effect < 0.0)
          {
            executedTest.EffectCss = "value-decrease";
          }
          else if (executedTest.Effect == 0.0)
          {
            executedTest.EffectCss = "value-nochange";
          }
          else
          {
            executedTest.EffectCss = "value-increase";
          }
        }
      }

      var content = new TestListViewModel
      {
        Items = items,
        TotalResults = activeTests.Count()
      };

      return Json(content);
    }

    private double GetWinningEffect(ITestConfiguration test)
    {
      var performanceForTest = PerformanceFactory.GetPerformanceForTest(test);
      if (performanceForTest.BestExperiencePerformance != null)
      {
        return performanceForTest.GetExperienceEffect(performanceForTest.BestExperiencePerformance.Combination, false);
      }

      return 0.0;
    }

    private int GetEstimatedDurationDays(Item hostItem, int experienceCount, TestDefinitionItem testDef)
    {
      string deviceName = string.Empty;
      if (testDef.Device.TargetItem != null)
      {
        deviceName = testDef.Device.TargetItem.Name;
      }

      var testRunEstimator = ContentTestingFactory.Instance.GetTestRunEstimator(testDef.Language, deviceName);
      testRunEstimator.HostItem = hostItem;

      var estimate = testRunEstimator.GetEstimate(experienceCount, 0.8, testDef.TrafficAllocationPercentage, testDef.ConfidenceLevelPercentage, testDef, TestMeasurement.Undefined);

      var timeSpan = testDef.StartDate.AddDays(estimate.EstimatedDayCount.HasValue ? estimate.EstimatedDayCount.Value : 0.0) - DateTime.UtcNow;
      int totalDays = (int)Math.Ceiling(timeSpan.TotalDays);
      if (totalDays < 1)
      {
        return int.Parse(testDef.MaxDuration);
      }

      var difference = DateTime.UtcNow - testDef.StartDate;
      return Math.Max(Math.Min(totalDays, int.Parse(testDef.MaxDuration) - difference.Days), int.Parse(testDef.MinDuration) - difference.Days);
    }
  }
}