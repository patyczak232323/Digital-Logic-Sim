using System;
using System.Collections.Generic;
using DLS.Description;

namespace DLS.SaveSystem
{
	// Example projects that come with Rewired. Their files live in
	// Assets/Resources/BundledProjects/<name> (ProjectDescription.json and Chips/*.json) and are
	// written by BundledProjectTests from the sources in Examples/.
	public static class BundledProjectInstaller
	{
		public const string ResourcesFolder = "BundledProjects";
		public static readonly string[] ProjectNames = { "Snake" };

		// Saves the project into the projects folder. Returns false, and changes nothing, if a
		// project with that name already exists.
		public static bool Install(string projectName, string projectJson, IEnumerable<string> chipJsons)
		{
			if (Loader.ProjectExists(projectName)) return false;

			ProjectDescription project = Serializer.DeserializeProjectDescription(projectJson);
			project.ProjectName = projectName;
			project.CreationTime = DateTime.Now;

			foreach (string chipJson in chipJsons)
			{
				Saver.SaveChip(Serializer.DeserializeChipDescription(chipJson), projectName);
			}

			// Saved last: a project only shows up in the list once its chips are in place.
			Saver.SaveProjectDescription(project);
			return true;
		}
	}
}
