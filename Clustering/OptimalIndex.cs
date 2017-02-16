﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HilbertTransformation;
using HilbertTransformation.Random;

namespace Clustering
{
	/// <summary>
	/// Find an optimal HilbertIndex for a given set of points.
	/// 
	/// Many HilbertIndex objects are created for the same set of points, each with a different permutation
	/// used in performing the Hilbert transformation on the points. Each permutation will cause the 
	/// points to be sorted in a different order. Some of these curves are better than others at visiting
	/// a cluster of points, moving from point to point within the cluster, then advancing to the next cluster
	/// without doubling back later to a cliuster already visited.
	/// 
	/// If the curve revisits a cluster multiple times after visiting other clusters, then that index is fragmented.
	/// The more fragmented an index is, the poorer the performance when we do an exhaustive construction 
	/// of the clusters. Whatever measure is used of quality should correlate well with the number of
	/// stripes found in the index. For example, if there are ten true clusters but the index finds twenty
	/// clusters, our fragmentation factor is two.
	/// 
	/// The metric used to evaluate the quality of the many indices to be tested may be supplied by the caller,
	/// as well as the strategy for randomizing the permutation used.
	/// </summary>
	public class OptimalIndex
	{
		/// <summary>
		/// Provides the results of the search.
		/// </summary>
		public class IndexFound: IComparable<IndexFound>
		{
			public Permutation<uint> PermutationUsed { get; private set; }

			public HilbertIndex Index { get; private set; }

			public int EstimatedClusterCount { get; private set; }

			/// <summary>
			/// The maximum distance between points (other than outliers) that should be clustered together.
			/// </summary>
			public long MergeSquareDistance { get; set; }

			public IndexFound(Permutation<uint> permutation, HilbertIndex index, int estimatedClusterCount, long mergeSquareDistance)
			{
				PermutationUsed = permutation;
				Index = index;
				EstimatedClusterCount = estimatedClusterCount;
				MergeSquareDistance = mergeSquareDistance;
			}

			/// <summary>
			/// Return true if this result is better than the other result.
			/// </summary>
			/// <param name="other">Other result for comparison.</param>
			/// <returns>True if an improvement was found.
			/// False if no improvement was found.
			/// </returns>
			public bool IsBetterThan(IndexFound other)
			{
				return CompareTo(other) < 0;
			}

			public int CompareTo(IndexFound other)
			{
				return EstimatedClusterCount.CompareTo(other.EstimatedClusterCount);
			}

			public override string ToString()
			{
				return $"[IndexFound: EstimatedClusterCount={EstimatedClusterCount}]";
			}
		}

		#region Attributes: Metric, PermutationStrategy, ParallelTrials, MaxIterations, MaxIterationsWithoutImprovement

		/// <summary>
		/// Scores how well a given index measures up. The lower the score, the better. 
		/// A low score is assumed to mean less fragmentation of the index.
		/// Also derives the MergeSquareDistance.
		/// </summary>
		Func<HilbertIndex, Tuple<int,long>> Metric { get; set; }

		/// <summary>
		/// Strategy to use for choosing a new permutation, given the previous best permutation,
		/// the number of dimensions for each point and the iteration number. 
		/// As the iteration number increases, it is pribably better to permute
		/// fewer coordinates and home in on a solution.
		/// 
		/// The second parameter is the number of dimensions per point.
		/// The third parameter is the iteration number.
		/// </summary>
		Func<Permutation<uint>, int, int, Permutation<uint>> PermutationStrategy { get; set; }

		/// <summary>
		/// Number of independent trials run in parallel using different permutations derived from the same
		/// starting permutation. It is not profitable for this to exceed the number of processors.
		/// </summary>
		public int ParallelTrials { get; set; } = 4;

		/// <summary>
		/// Maximum number of iterations to perform before stopping.
		/// 
		/// The most total trials that will be done is ParallelTrials * MaxIterations.
		/// </summary>
		public int MaxIterations { get; set; } = 10;

		/// <summary>
		/// If several iterations are performed with no improvement (i.e. reduction) in score,
		/// searching will halt.
		/// </summary>
		public int MaxIterationsWithoutImprovement { get; set; } = 3;


		#endregion

		/// <summary>
		/// A simple strategy that scrambles all the coordinates the first iteration,
		/// then half the coordinates the next iteration, 
		/// then a quarter the dimensions the next iteration, etc until five dimensions are reached.
		/// </summary>
		public static Func<Permutation<uint>, int, int, Permutation<uint>> ScrambleHalfStrategy = 
			(previousPermutation, dimensions, iteration) =>
		{
			// Assume that iteration is zero-based.
			var dimensionsToScramble = Math.Max(Math.Min(dimensions, 5), dimensions / (1 << iteration));
			return previousPermutation.Scramble(dimensionsToScramble);
		};

		#region Constructors

		/// <summary>
		/// Create an optimizer which finds the curve that minimizes the number of clusters found using means
		/// supplied by the caller.
		/// </summary>
		/// <param name="metric">Evaluates the quality of the HilbertIndex derived using a given permutation.</param>
		/// <param name="strategy">Strategy to employ that decides how many dimensions to scramble during each iteration.</param>
		public OptimalIndex(Func<HilbertIndex, Tuple<int,long>> metric, Func<Permutation<uint>, int, int, Permutation<uint>> strategy)
		{
			Metric = metric;
			PermutationStrategy = strategy;
		}

		/// <summary>
		/// Create an optimizer which finds the curve that minimizes the number of clusters found using a ClusterCounter.
		/// </summary>
		/// <param name="outlierSize">OutlierSize to use with the ClusterCounter.</param>
		/// <param name="noiseSkipBy">NoiseSkipBy to use with the ClusterCounter.</param>
		/// <param name="strategy">Strategy to employ that decides how many dimensions to scramble during each iteration.</param>
		public OptimalIndex(int outlierSize, int noiseSkipBy, Func<Permutation<uint>, int, int, Permutation<uint>> strategy)
		{
			var maxOutliers = outlierSize;
			var skip = noiseSkipBy;
			Metric = (HilbertIndex index) =>
			{
				var counter = new ClusterCounter { OutlierSize = maxOutliers, NoiseSkipBy = skip };
				var counts = counter.Count(index.SortedPoints);
				return new Tuple<int, long>(counts.CountExcludingOutliers, counts.MaximumSquareDistance);
			};
			PermutationStrategy = strategy;
		}

		#endregion

		/// <summary>
		/// Search many HilbertIndex objects, each based on a different permutation of the dimensions, and
		/// keep the one yielding the best Metric, which is likely the one that estimates the lowest value 
		/// for the number of clusters.
		/// </summary>
		/// <param name="points">Points to index.</param>
		/// <param name="startingPermutation">Starting permutation.</param>
		/// <returns>The best index found and the permutation that generated it.</returns>
		public IndexFound Search(IList<HilbertPoint> points, Permutation<uint> startingPermutation = null)
		{
			var found = SearchMany(points, 1, startingPermutation);
			return found.First();
		}

		/// <summary>
		/// Search many HilbertIndex objects, each based on a different permutation of the dimensions, and
		/// keep the ones yielding the best Metrics, likely those that estimate the lowest values
		/// for the number of clusters.
		/// </summary>
		/// <param name="points">Points to index.</param>
		/// <param name="indexCount">Number of the best indices to return. 
		/// For example, if this is 10, then the 10 indices with the lowest scores will be kept.</param>
		/// <param name="startingPermutation">Starting permutation.</param>
		/// <returns>The best indices found and the permutations that generated them.
		/// THe first item in the returned list is the best of the best, and the last is the worst of the best.</returns>
		public IList<IndexFound> SearchMany(IList<HilbertPoint> points, int indexCount, Permutation<uint> startingPermutation = null)
		{
			if (points.Count() < 10)
				throw new ArgumentException("List has too few elements", nameof(points));
			var queue = new BinaryHeap<IndexFound>(BinaryHeapType.MaxHeap, indexCount);
			int dimensions = points[0].Dimensions;
			var bitsPerDimension = points[0].BitsPerDimension;
			if (startingPermutation == null)
				startingPermutation = new Permutation<uint>(dimensions);
			var firstIndex = new HilbertIndex(points, startingPermutation);
			// Measure our first index, then loop through random permutations 
			// looking for a better one, always accumulating the best in results.
			var metricResults = Metric(firstIndex);
			var bestResults = new IndexFound(startingPermutation, firstIndex, metricResults.Item1, metricResults.Item2);
			queue.AddRemove(bestResults);

			var iterationsWithoutImprovement = 0;
			for (var iteration = 0; iteration < MaxIterations; iteration++)
			{
				var improvedCount = 0;
				var startFromPermutation = bestResults.PermutationUsed;
				Parallel.For(0, ParallelTrials,
					i =>
				{
					Permutation<uint> permutationToTry;
					// This locking is needed because we use a static random number generator to create a new permutation.
					// It is more expensive to make the random number generator threadsafe than to make this loop threadsafe.
					lock (startFromPermutation)
					{
						permutationToTry = PermutationStrategy(startFromPermutation, dimensions, iteration);
					}
					var indexToTry = new HilbertIndex(points, permutationToTry);
					metricResults = Metric(indexToTry);
					var resultsToTry = new IndexFound(permutationToTry, indexToTry, metricResults.Item1, metricResults.Item2);

					lock(queue)
					{
						queue.AddRemove(resultsToTry);
						var improved = resultsToTry.IsBetterThan(bestResults);
						if (improved)
						{
							bestResults = resultsToTry;
							Interlocked.Add(ref improvedCount, 1);
							Console.Write($"Cluster count Improved to: {bestResults}");
						} 
					}

				});
				if (improvedCount > 0)
					iterationsWithoutImprovement = 0;
				else
					iterationsWithoutImprovement++;
				if (iterationsWithoutImprovement >= MaxIterationsWithoutImprovement)
					break;
			}
			return queue.RemoveAll().Reverse().ToList();
		}

		/// <summary>
		/// Using default values for many parameters, search many HilbertIndex objects, each based on a different permutation of the dimensions, and
		/// keep the one yielding the best Metric, which is the one that estimates the lowest value 
		/// for the number of clusters.
		/// </summary>
		/// <param name="points">Points to index.</param>
		/// <param name="outlierSize">OutlierSize that discriminates between clusters worth counting and those that are not.</param>
		/// <param name="noiseSkipBy">NoiseSkipBy value to help smooth out calculations in the presence of noisy data.</param>
		/// <param name="maxTrials">Max trials to attempt. This equals MaxIterations * ParallelTrials (apart from rounding).</param>
		/// <param name="maxIterationsWithoutImprovement">Max iterations without improvement.
		/// Stops searching early if no improvement is detected.</param>
		public static IndexFound Search(IList<HilbertPoint> points, int outlierSize, int noiseSkipBy, int maxTrials, int maxIterationsWithoutImprovement = 3)
		{
			var parallel = 4;
			var optimizer = new OptimalIndex(outlierSize, noiseSkipBy, ScrambleHalfStrategy)
			{
				MaxIterations = (maxTrials + (parallel / 2)) / parallel,
				MaxIterationsWithoutImprovement = maxIterationsWithoutImprovement,
				ParallelTrials = parallel
			};
			return optimizer.Search(points);
		}

		public static IList<IndexFound> SearchMany(IList<HilbertPoint> points, int indexCount, int outlierSize, int noiseSkipBy, int maxTrials, int maxIterationsWithoutImprovement = 3)
		{
			var parallel = 4;
			var optimizer = new OptimalIndex(outlierSize, noiseSkipBy, ScrambleHalfStrategy)
			{
				MaxIterations = (maxTrials + (parallel / 2)) / parallel,
				MaxIterationsWithoutImprovement = maxIterationsWithoutImprovement,
				ParallelTrials = parallel
			};
			return optimizer.SearchMany(points, indexCount);
		}

	}
}