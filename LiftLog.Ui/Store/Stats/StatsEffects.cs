using System.Collections.Immutable;
using Fluxor;
using Google.Protobuf.WellKnownTypes;
using LiftLog.Lib;
using LiftLog.Lib.Models;
using LiftLog.Ui.Models;
using LiftLog.Ui.Repository;
using LiftLog.Ui.Services;

namespace LiftLog.Ui.Store.Stats;

public class StatsEffects(IState<StatsState> state, IProgressRepository progressRepository)
{
    [EffectMethod]
    public async Task HandleFetchStats(FetchOverallStatsAction _, IDispatcher dispatcher)
    {
        if (!state.Value.IsDirty || state.Value.IsLoading)
        {
            return;
        }

        dispatcher.Dispatch(new SetStatsIsLoadingAction(true));

        var latestTime = DateOnly.FromDateTime(DateTime.Now);
        var earliestTime = DateOnly.FromDateTime(
            latestTime.ToDateTime(TimeOnly.MinValue) - state.Value.OverallViewTime
        );

        await Task.Run(async () =>
        {
            var sessions = await progressRepository
                .GetOrderedSessions()
                .Where(session => session.Date >= earliestTime && session.Date <= latestTime)
                .Where(x => x.RecordedExercises.Any())
                .ToListAsync();

            var bodyweightStats = CreateBodyweightStatistic(sessions);
            var sessionStats = sessions
                .GroupBy(session => session.Blueprint.Name)
                .Select(CreateSessionStatistic)
                .ToImmutableList();

            var exerciseStats = sessions
                .SelectMany(x =>
                    x.RecordedExercises.Where(y => y.LastRecordedSet?.Set is not null)
                        .Select(ex => new DatedRecordedExercise(
                            x.Date.ToDateTime(ex.LastRecordedSet!.Set!.CompletionTime),
                            ex
                        ))
                )
                .GroupBy(x => NormalizeName(x.RecordedExercise.Blueprint.Name))
                .Select(CreateExerciseStatistic)
                .ToImmutableList();

            var averageTimeBetweenSets = sessions
                .SelectMany(x => x.RecordedExercises)
                .Select(x =>
                    x.PotentialSets.Select(set => set.Set?.CompletionTime.ToTimeSpan())
                        .WhereNotNull()
                        .Order()
                        .Pairwise((a, b) => b - a)
                )
                .SelectMany(x => x)
                .Aggregate(
                    (TimeSpan.Zero, 0),
                    (acc, x) => (acc.Item1 + x, acc.Item2 + 1),
                    acc => acc.Item1 / acc.Item2
                );

            var averageSessionLength = sessions
                .Select(session => session.SessionLength)
                .WhereNotNull()
                .Aggregate(
                    (TimeSpan.Zero, 0),
                    (acc, x) => (acc.Item1 + x, acc.Item2 + 1),
                    acc => acc.Item1 / acc.Item2
                );

            var exerciseMostTimeSpent = sessions
                .SelectMany(x => x.RecordedExercises)
                .Where(x => x.LastRecordedSet?.Set is not null)
                .GroupBy(x => NormalizeName(x.Blueprint.Name))
                .Select(x => new TimeSpentExercise(
                    x.First().Blueprint.Name,
                    x.Select(x => x.TimeSpent).Aggregate((a, b) => a + b)
                ))
                .MaxBy(x => x.TimeSpent);

            var heaviestLift = sessions
                .SelectMany(x => x.RecordedExercises)
                .Where(x => x.FirstRecordedSet is not null)
                .MaxBy(x => x.Weight);

            dispatcher.Dispatch(
                new SetOverallStatsAction(
                    new GranularStatisticView(
                        AverageTimeBetweenSets: averageTimeBetweenSets,
                        AverageSessionLength: averageSessionLength,
                        HeaviestLift: heaviestLift,
                        ExerciseMostTimeSpent: exerciseMostTimeSpent,
                        ExerciseStats: exerciseStats,
                        SessionStats: sessionStats,
                        BodyweightStats: bodyweightStats
                    )
                )
            );

            dispatcher.Dispatch(new SetStatsIsDirtyAction(false));
            dispatcher.Dispatch(new SetStatsIsLoadingAction(false));
        });
    }

    private static string NormalizeName(string name)
    {
        var lowerName = name.ToLower().Trim().Replace("flies", "flys").Replace("flyes", "flys");
        var withoutPlural = lowerName switch
        {
            string when lowerName.EndsWith("es") => lowerName[..^2],
            string when lowerName.EndsWith('s') => lowerName[..^1],
            _ => lowerName
        };

        return withoutPlural;
    }

    private static StatisticOverTime CreateBodyweightStatistic(IEnumerable<Session> sessions)
    {
        return new(
            "Bodyweight",
            sessions
                .Where(x => x.Bodyweight is not null)
                .Select(session => new TimeTrackedStatistic(
                    session.Date.ToDateTime(TimeOnly.MinValue),
                    session.Bodyweight!.Value
                ))
                .ToImmutableList()
        );
    }

    private static StatisticOverTime CreateSessionStatistic(IGrouping<string, Session> sessions)
    {
        return new StatisticOverTime(
            sessions.Key,
            sessions
                .Select(session => new TimeTrackedStatistic(
                    session.Date.ToDateTime(TimeOnly.MinValue),
                    session.TotalWeightLifted
                ))
                .ToImmutableList()
        );
    }

    private static ExerciseStatisticOverTime CreateExerciseStatistic<T>(
        IGrouping<T, DatedRecordedExercise> exercises
    )
    {
        return new ExerciseStatisticOverTime(
            exercises.First().RecordedExercise.Blueprint.Name,
            Statistics: exercises
                .Select(exercise => new TimeTrackedStatistic(
                    exercise.DateTime,
                    exercise.RecordedExercise.Weight
                ))
                .ToImmutableList(),
            OneRepMax: exercises.First().RecordedExercise.OneRepMax
        );
    }
}
