using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FuzzyMatch;
using ParallelPatterns.Common;
using ParallelPatterns.Common.TaskEx;
#if FSHARP
using static ParallelPatterns.Fsharp.TaskCompositionEx.TaskEx;
#else
using ParallelPatterns.TaskComposition;
#endif
using static FuzzyMatch.JaroWinklerModule.FuzyMatchStructures;
using static ParallelPatterns.Common.FuzzyMatchHelpers;

namespace ParallelPatterns
{
    public partial class ParallelFuzzyMatch
    {
        public static IDictionary<string, HashSet<string>>
            RunFuzzyMatchSequential(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {
            // Sequential workflow -> how can we parallelize this work?
            // The collection 'matchSet' cannot be shared among threads  

            var matchSet = new HashSet<WordDistanceStruct>();

            foreach (var file in files)
            {
                string readText = File.ReadAllText(file);

                var words = readText.Split(punctuation.Value)
                    .Where(w => !IgnoreWords.Contains(w))
                    .AsSet();

                foreach (var wl in wordsLookup)
                {
                    var bestMatch = JaroWinklerModule.bestMatch(words, wl, threshold);
                    matchSet.AddRange(bestMatch);
                }
            }

            return PrintSummary(matchSet);
        }
        
        public static async Task<IDictionary<string, HashSet<string>>>
            RunFuzzyMatchTaskContinuation(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {
            // Let's start by converting the I/O operation to be asynchronous 
            // The continuation passing style avoids to block any threads  
            // 
            // What about the error handlimg ? and cancellation (if any) ?

            var matchSet = new HashSet<WordDistanceStruct>();

            foreach (var file in files)
            {
                var readFileTask = File.ReadAllTextAsync(file);

                IEnumerable<WordDistanceStruct[]> bestMatches =
                    await readFileTask
                        .ContinueWith(readText =>
                        {
                            return WordRegex.Value.Split(readText.Result)
                                .Where(w => !IgnoreWords.Contains(w));
                        })
                        .ContinueWith(words =>
                        {
                            var tasks = (from wl in wordsLookup
                                select JaroWinklerModule.bestMatchTask(words.Result, wl, threshold)).ToList();

                            return Task.WhenAll(tasks);
                        }).Unwrap();

                matchSet.AddRange(bestMatches.Flatten());
            }

            return PrintSummary(matchSet);
        }


        public static async Task<IDictionary<string, HashSet<string>>>
            RunFuzzyMatchBetterTaskContinuation(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {
            // Ideally, we should handle potential errors or cancellations
            // This is a lot of code which goes against the DRY principal

            var matchSet = new HashSet<WordDistanceStruct>();

            foreach (var file in files)
            {
                var readFileTask = File.ReadAllTextAsync(file);
                var bestMatches = await readFileTask
                    .ContinueWith(readText =>
                    {
                        switch (readText.Status)
                        {
                            case TaskStatus.Faulted:
                                Exception ex = readText.Exception;
                                while (ex is AggregateException && ex.InnerException != null)
                                    ex = ex.InnerException;
                                // do something with ex
                                return null;
                            case TaskStatus.Canceled:
                                // do something because Task cancelled
                                return null;
                            default:
                                return WordRegex.Value.Split(readText.Result)
                                    .Where(w => !IgnoreWords.Contains(w));
                        }
                    })
                    .ContinueWith(words =>
                    {
                        switch (words.Status)
                        {
                            case TaskStatus.Faulted:
                                Exception ex = words.Exception;
                                while (ex is AggregateException && ex.InnerException != null)
                                    ex = ex.InnerException;
                                // do something with ex
                                return null;
                            case TaskStatus.Canceled:
                                // do something because Task cancelled
                                return null;
                            default:
                                return wordsLookup.Traverse(wl =>
                                    JaroWinklerModule.bestMatchTask(words.Result, wl, threshold));
                        }
                    }).Unwrap();

                matchSet.AddRange(bestMatches.Flatten());
            }

            return PrintSummary(matchSet);
        }


        public static  Task<IDictionary<string, HashSet<string>>>
            RunFuzzyMatchTaskComposition(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {

            
            return
                files.Traverse(file => File.ReadAllTextAsync(file))
                    .Then(fileContent =>
                         fileContent
                             .SelectMany(text => WordRegex.Value.Split(text))
                             .Where(w => !IgnoreWords.Contains(w))
                             .AsSet()
                    )
                    .Then(wordsSplit =>
                        wordsLookup.Traverse(wl => JaroWinklerModule.bestMatchTask(wordsSplit, wl, threshold))
                    )
                    .Then(matcheSet => PrintSummary(matcheSet.Flatten().AsSet()));

            // NOTES
            // In this scenario, and only for demo purposes, we are reading the text asynchronously 
            // in one operation, and then we are treating the text as a unique string. 
            // In the case that the text is a large string, there are some performance penalties especially  
            // during the Regex Split. A better approach is to read text files in line and run the Regex against
            // chunks of strings.
            // One solution is to create a Task that reads, splits and flattens the input text 
            // in one operation. The method "ReadFileLinesAndFlatten" ( in the "TaskEx" static class )
            // implements this design. 
            // Feel free to check the method and use it if you would like.
            // Here is the code that replaces the previus code.
            
            //return
            //    files.Traverse(file => ReadFileLinesAndFlatten(file))
            //        .Then(wordsSplit =>
            //        {
            //            var words = wordsSplit.Flatten();
            //            return wordsLookup.Traverse(wl => JaroWinklerModule.bestMatchTask(words, wl, threshold));
            //        })
            //        .Then(matcheSet => PrintSummary(matcheSet.Flatten().AsSet()));
        }

        // Utility method
        private static Task<HashSet<string>> ReadFileLinesAndFlatten(string file)
        {
            var tcs = new TaskCompletionSource<HashSet<string>>();
            Task<string[]> readFileLinesTask = File.ReadAllLinesAsync(file);

            readFileLinesTask.ContinueWith(fs =>
                fs.Result.Traverse(line =>
                    WordRegex.Value.Split(line)
                        .Where(w => !IgnoreWords.Contains(w))
                )
            ).ContinueWith(t =>
                tcs.FromTask(t, task => task.Result.Flatten().AsSet())
            );
            return tcs.Task;
        }

        
        public static async Task<IDictionary<string, HashSet<string>>>
            RunFuzzyMatchPLINQ(
                string[] wordsLookup, 
                IEnumerable<string> files)
        {
           
            var matchSet = await (
                from contentFile in files.Traverse(f => File.ReadAllTextAsync(f))
                
                from words in contentFile.Traverse(text =>
                    WordRegex.Value.Split(text).Where(w => !IgnoreWords.Contains(w)))
                let wordSet = words.Flatten().AsSet()
            
                from bestMatch in wordsLookup.Traverse(wl => JaroWinklerModule.bestMatchTask(wordSet, wl, threshold))
                select bestMatch.Flatten());
            
            
            // NOTES
            // Here the code that leverages the "ReadFileLinesAndFlatten" method
            
            //var matchSet = await (
            //    from contentFile in files.Traverse(f => ReadFileLinesAndFlatten(f))
            //    let wordSet = contentFile.Flatten().AsSet()
            //    from bestMatch in wordsLookup.Traverse(wl => JaroWinklerModule.bestMatchTask(wordSet, wl, threshold))
            //    select bestMatch.Flatten());

            return PrintSummary(matchSet.AsSet());
        }
    }
}