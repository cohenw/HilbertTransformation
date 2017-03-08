﻿using System;
using Clustering;
using HilbertTransformationTests.Data;
using NUnit.Framework;

namespace HilbertTransformationTests
{
	[TestFixture]
	public class SlashCommandTests
	{
		/// <summary>
		/// Prepare the SlashConfig and data without reading from files,
		/// and cluster the data without writing the results.
		/// </summary>
		[Test]
		public void ClusterWithoutFiles()
		{
			var bitsPerDimension = 10;
			var data = new GaussianClustering
			{
				ClusterCount = 20,
				Dimensions = 50,
				MaxCoordinate = (1 << bitsPerDimension) - 1,
				MinClusterSize = 200,
				MaxClusterSize = 600
			};
			var expectedClassification = data.MakeClusters();

			var config = new SlashConfig() { AcceptableBCubed = 0.98 };
			config.Index.BitsPerDimension = bitsPerDimension;
			config.UseNoFiles();
			var command = new SlashCommand(SlashCommand.CommandType.Cluster, config)
			{
				InputFile = null,
				OutputFile = null
			};
			// Need to put this here, because the command initializes the logger differently.
			Logger.SetupForTests(null);
			command.LoadData(expectedClassification);

			command.Execute();

			Assert.IsTrue(command.IsClassificationAcceptable, $"The BCubed value of {command.MeasuredChange.BCubed} was not good enough.");

		}
	}
}
