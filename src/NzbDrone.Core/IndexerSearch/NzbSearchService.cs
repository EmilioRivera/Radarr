using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.AlternativeTitles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;

namespace NzbDrone.Core.IndexerSearch
{
    public interface ISearchForNzb
    {
        List<DownloadDecision> MovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch);
        List<DownloadDecision> MovieSearch(Movie movie, bool userInvokedSearch, bool interactiveSearch);
    }

    public class NzbSearchService : ISearchForNzb
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IMakeDownloadDecision _makeDownloadDecision;
        private readonly IMovieService _movieService;
        private readonly IProfileService _profileService;
        private readonly Logger _logger;

        public NzbSearchService(IIndexerFactory indexerFactory,
                                IMakeDownloadDecision makeDownloadDecision,
                                IMovieService movieService,
                                IProfileService profileService,
                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _makeDownloadDecision = makeDownloadDecision;
            _movieService = movieService;
            _profileService = profileService;
            _logger = logger;
        }

        public List<DownloadDecision> MovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch)
        {
            var movie = _movieService.GetMovie(movieId);

            return MovieSearch(movie, userInvokedSearch, interactiveSearch);
        }

        public List<DownloadDecision> MovieSearch(Movie movie, bool userInvokedSearch, bool interactiveSearch)
        {
            var searchSpec = Get<MovieSearchCriteria>(movie, userInvokedSearch, interactiveSearch);

            return Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
        }

        private TSpec Get<TSpec>(Movie movie, bool userInvokedSearch, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec()
            {
                Movie = movie,
                UserInvokedSearch = userInvokedSearch,
                InteractiveSearch = interactiveSearch,
                SceneTitles = new List<string> { movie.Title }
            };

            var profile = _profileService.Get(movie.ProfileId);

            var wantedTitleLanguages = profile.FormatItems.Where(i => i.Score > 0).Select(item => item.Format)
                .SelectMany(format => format.Specifications)
                .Where(specification => specification is LanguageSpecification && !specification.Negate)
                .Cast<LanguageSpecification>()
                .Where(specification => specification.Value > 0)
                .Select(specification => (Language)specification.Value)
                .Distinct()
                .ToList();

            wantedTitleLanguages.Add(profile.Language);

            var translations = new List<string>();

            //Add Translation of wanted language to search query
            foreach (var translation in movie.AlternativeTitles.Where(a => wantedTitleLanguages.Contains(a.Language) && a.SourceType == SourceType.Translation))
            {
                translations.Add(translation.Title);
            }

            spec.SceneTitles.AddRange(translations.Distinct());

            return spec;
        }

        private List<DownloadDecision> Dispatch(Func<IIndexer, IEnumerable<ReleaseInfo>> searchAction, SearchCriteriaBase criteriaBase)
        {
            var indexers = criteriaBase.InteractiveSearch ?
                _indexerFactory.InteractiveSearchEnabled() :
                _indexerFactory.AutomaticSearchEnabled();

            var reports = new List<ReleaseInfo>();

            _logger.ProgressInfo("Searching {0} indexers for {1}", indexers.Count, criteriaBase);

            var taskList = new List<Task>();
            var taskFactory = new TaskFactory(TaskCreationOptions.LongRunning, TaskContinuationOptions.None);

            foreach (var indexer in indexers)
            {
                var indexerLocal = indexer;

                taskList.Add(taskFactory.StartNew(() =>
                {
                    try
                    {
                        var indexerReports = searchAction(indexerLocal);

                        lock (reports)
                        {
                            reports.AddRange(indexerReports);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Error while searching for {0}", criteriaBase);
                    }
                }).LogExceptions());
            }

            Task.WaitAll(taskList.ToArray());

            _logger.Debug("Total of {0} reports were found for {1} from {2} indexers", reports.Count, criteriaBase, indexers.Count);

            return _makeDownloadDecision.GetSearchDecision(reports, criteriaBase).ToList();
        }
    }
}
