using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using FuzzyMatch;
using Microsoft.FSharp.Core;
using ParallelPatterns.Common;
using ParallelPatterns.Common.TaskEx;
using ParallelPatterns.Fsharp;
using ParallelPatterns.TaskComposition;
using static FuzzyMatch.JaroWinklerModule.FuzyMatchStructures;
using static ParallelPatterns.Common.FuzzyMatchHelpers;

namespace ParallelPatterns
{
    public partial class ParallelFuzzyMatch
    {
        public static void RunFuzzyMatchPipeline(
            string[] wordsLookup,
            IList<string> files)
        {
            var pipeline = Pipeline<string, string[]>.Create(file => File.ReadAllLinesAsync(file));

            pipeline
                .Then(lines =>
                    lines.SelectMany(l => l.Split(punctuation.Value)
                        .Where(w => !IgnoreWords.Contains(w))).AsSet()
                )
                .Then(wordSet =>
                    wordsLookup.Traverse(wl => JaroWinklerModule.bestMatchTask(wordSet, wl, threshold))
                )
                .Then(matcheSet =>
                    PrintSummary(matcheSet.Flatten().AsSet())
                );

            foreach (var file in files)
            {
                Console.WriteLine($"analyzing file {file}");
                pipeline.Enqueue(file);
            }
            // End C# Pipeline
            
  

        }


        public static async Task<IDictionary<string, HashSet<string>>>
            RunFuzzyMatchTaskProcessAsCompleteBasic(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {
            // An alternative pattern to parallize the FuzzyMatch is the "Procces as complete"
            // The idea of this pattern is to start the execution of all the operations (tasks) 
            // at the same time, and then proccess them as they complete instead of waiting for all the operations
            // to be completed before continuing.
            // In other words, this pattern returns a sequence of tasks which will be observed to complete with the same set 
            // of results as the given input tasks, but in the order in which the original tasks complete.
            //
            // Here a simple implementation ::

            var matchSet = new HashSet<WordDistanceStruct>();

            var readFileTasks = 
                (from file in files 
                 select File.ReadAllTextAsync(file)
                ).ToList();

            while (readFileTasks.Count > 0)
            {
                await Task.WhenAny(readFileTasks)
                    .ContinueWith(async readTask =>
                    {
                        var finishedReadTask = readTask.Result;
                        readFileTasks.Remove(finishedReadTask);

                        var words = WordRegex.Value
                            .Split(finishedReadTask.Result)
                            .Where(w => !IgnoreWords.Contains(w));

                        var matchTasks = 
                            (from wl in wordsLookup
                             select JaroWinklerModule.bestMatchTask(words, wl, threshold)
                            ).ToList();

                        while (matchTasks.Count > 0)
                        {
                            await Task.WhenAny(matchTasks).ContinueWith(matchTask =>
                            {
                                var finishedMatchTask = matchTask.Result;
                                matchTasks.Remove(finishedMatchTask);
                               
                                matchSet.AddRange(finishedMatchTask.Result);
                            });
                        }
                    });
            }

            return PrintSummary(matchSet);
        }


        public static async Task<IDictionary<string, HashSet<string>>>
            RunFuzzyMatchTaskProcessAsCompleteAbstracted(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {
            var matchSet = new HashSet<WordDistanceStruct>();

            foreach (var textTask in files.ContinueAsComplete(file => File.ReadAllTextAsync(file)))
            {
                var text = await textTask;

                var words = WordRegex.Value
                    .Split(text)
                    .Where(w => !IgnoreWords.Contains(w))
                    .AsSet();

                foreach (var matchTask in wordsLookup.ContinueAsComplete(
                    wl => JaroWinklerModule.bestMatchTask(words, wl, threshold)))
                {
                    matchSet.AddRange(await matchTask);
                }
            }

            return PrintSummary(matchSet);
        }
    }
}