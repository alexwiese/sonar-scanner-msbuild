﻿/*
 * SonarQube Scanner for MSBuild
 * Copyright (C) 2015-2017 SonarSource SA and Microsoft Corporation
 * mailto: contact AT sonarsource DOT com
 *
 * Licensed under the MIT License.
 * See LICENSE file in the project root for full license information.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */

using SonarQube.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SonarScanner.Shim
{
    public static class PropertiesFileGenerator
    {
        private const string ProjectPropertiesFileName = "sonar-project.properties";

        public const string VSBootstrapperPropertyKey = "sonar.visualstudio.enable";

        public const string ReportFileCsharpPropertyKey = "sonar.cs.roslyn.reportFilePath";
        public const string ReportFileVbnetPropertyKey = "sonar.vbnet.roslyn.reportFilePath";

        #region Public methods

        /// <summary>
        /// Locates the ProjectInfo.xml files and uses the information in them to generate
        /// a sonar-scanner properties file
        /// </summary>
        /// <returns>Information about each of the project info files that was processed, together with
        /// the full path to generated file.
        /// Note: the path to the generated file will be null if the file could not be generated.</returns>
        public static ProjectInfoAnalysisResult GenerateFile(AnalysisConfig config, ILogger logger)
        {
            return GenerateFile(config, logger, new RoslynV1SarifFixer());
        }

        public /* for test */ static ProjectInfoAnalysisResult GenerateFile(AnalysisConfig config, ILogger logger, IRoslynV1SarifFixer fixer)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            string fileName = Path.Combine(config.SonarOutputDir, ProjectPropertiesFileName);
            logger.LogDebug(Resources.MSG_GeneratingProjectProperties, fileName);

            IEnumerable<ProjectInfo> projects = ProjectLoader.LoadFrom(config.SonarOutputDir);
            if (projects == null || !projects.Any())
            {
                logger.LogError(Resources.ERR_NoProjectInfoFilesFound);
                return new ProjectInfoAnalysisResult();
            }

            TryFixSarifReports(logger, projects, fixer);

            string projectBaseDir = ComputeProjectBaseDir(config, projects);

            PropertiesWriter writer = new PropertiesWriter(config);

            ProjectInfoAnalysisResult result = ProcessProjectInfoFiles(projects, writer, logger, projectBaseDir);
            writer.WriteSonarProjectInfo(projectBaseDir, result.SharedFiles);

            IEnumerable<ProjectInfo> validProjects = result.GetProjectsByStatus(ProjectInfoValidity.Valid);

            if (validProjects.Any() || result.SharedFiles.Any())
            {
                // Handle global settings
                AnalysisProperties properties = GetAnalysisPropertiesToWrite(config, logger);
                writer.WriteGlobalSettings(properties);

                string contents = writer.Flush();

                result.FullPropertiesFilePath = fileName;
                File.WriteAllText(result.FullPropertiesFilePath, contents, Encoding.ASCII);
            }
            else
            {
                // if the user tries to build multiple configurations at once there will be duplicate projects
                if (result.GetProjectsByStatus(ProjectInfoValidity.DuplicateGuid).Any())
                {
                    logger.LogError(Resources.ERR_NoValidButDuplicateProjects);
                }
                else
                {
                    logger.LogError(Resources.ERR_NoValidProjectInfoFiles);
                }
            }
            return result;
        }

        /// <summary>
        /// Appends the sonar.projectBaseDir value. This is calculated as follows:
        /// 1. the user supplied value, or if none
        /// 2. the sources directory if running from TFS Build or XAML Build, or
        /// 3. the common root path of projects, or if there isn't any
        /// 4. the .sonarqube/out directory
        /// </summary>
        public static string ComputeProjectBaseDir(AnalysisConfig config, IEnumerable<ProjectInfo> projects)
        {
            string projectBaseDir = config.GetConfigValue(SonarProperties.ProjectBaseDir, null);
            if (!string.IsNullOrWhiteSpace(projectBaseDir))
            {
                return projectBaseDir;
            }

            projectBaseDir = config.SourcesDirectory;
            if (!string.IsNullOrWhiteSpace(projectBaseDir))
            {
                return projectBaseDir;
            }

            projectBaseDir = GetCommonRootOfProjects(projects);
            if (!string.IsNullOrWhiteSpace(projectBaseDir))
            {
                return projectBaseDir;
            }

            return config.SonarOutputDir;
        }

        #endregion

        #region Private methods

        private static string GetCommonRootOfProjects(IEnumerable<ProjectInfo> projects)
        {
            var projectDirectoryParts = projects
                .Select(p => p.GetProjectDirectory())
                .Select(p => p.Split(Path.DirectorySeparatorChar))
                .ToList();

            if (projectDirectoryParts.Count == 0)
            {
                return string.Empty;
            }

            var commonParts = projectDirectoryParts
                .OrderBy(p => p.Length)
                .First()
                .TakeWhile((element, index) => projectDirectoryParts.All(p => p[index] == element))
                .ToArray();

            return string.Join(Path.DirectorySeparatorChar.ToString(), commonParts);
        }

        /// <summary>
        /// Loads SARIF reports from the given projects and attempts to fix
        /// improper escaping from Roslyn V1 (VS 2015 RTM) where appropriate.
        /// </summary>
        private static void TryFixSarifReports(ILogger logger, IEnumerable<ProjectInfo> projects, IRoslynV1SarifFixer fixer /* for test */)
        {
            // attempt to fix invalid project-level SARIF emitted by Roslyn 1.0 (VS 2015 RTM)
            foreach (ProjectInfo project in projects)
            {
                TryFixSarifReport(logger, project, fixer, RoslynV1SarifFixer.CSharpLanguage, ReportFileCsharpPropertyKey);
                TryFixSarifReport(logger, project, fixer, RoslynV1SarifFixer.VBNetLanguage, ReportFileVbnetPropertyKey);
            }
        }

        private static void TryFixSarifReport(ILogger logger, ProjectInfo project, IRoslynV1SarifFixer fixer, string language, string reportFilePropertyKey)
        {
            Property reportPathProperty;
            bool tryResult = project.TryGetAnalysisSetting(reportFilePropertyKey, out reportPathProperty);
            if (tryResult)
            {
                string reportPath = reportPathProperty.Value;
                string fixedPath = fixer.LoadAndFixFile(reportPath, language, logger);

                if (!reportPath.Equals(fixedPath)) // only need to alter the property if there was no change
                {
                    // remove the property ahead of changing it
                    // if the new path is null, the file was unfixable and we should leave the property out
                    project.AnalysisSettings.Remove(reportPathProperty);

                    if (fixedPath != null)
                    {
                        // otherwise, set the property value (results in no change if the file was already valid)
                        Property newReportPathProperty = new Property();
                        newReportPathProperty.Id = reportFilePropertyKey;
                        newReportPathProperty.Value = fixedPath;
                        project.AnalysisSettings.Add(newReportPathProperty);
                    }
                }
            }
        }

        private static ProjectInfoAnalysisResult ProcessProjectInfoFiles(IEnumerable<ProjectInfo> projects, PropertiesWriter writer, ILogger logger, string projectBaseDir)
        {
            ProjectInfoAnalysisResult result = new ProjectInfoAnalysisResult();

            foreach (ProjectInfo projectInfo in projects)
            {
                ProjectInfoValidity status = ClassifyProject(projectInfo, projects, logger);

                if (status == ProjectInfoValidity.Valid)
                {
                    IEnumerable<string> files = GetFilesToAnalyze(projectInfo, logger, projectBaseDir, result);
                    if (files == null || !files.Any())
                    {
                        status = ProjectInfoValidity.NoFilesToAnalyze;
                    }
                    else
                    {
                        string fxCopReport = TryGetFxCopReport(projectInfo, logger);
                        string vsCoverageReport = TryGetCodeCoverageReport(projectInfo, logger);
                        writer.WriteSettingsForProject(projectInfo, files, fxCopReport, vsCoverageReport);
                    }
                }

                result.Projects.Add(projectInfo, status);
            }
            return result;
        }

        private static ProjectInfoValidity ClassifyProject(ProjectInfo projectInfo, IEnumerable<ProjectInfo> projects, ILogger logger)
        {
            if (projectInfo.IsExcluded)
            {
                logger.LogInfo(Resources.MSG_ProjectIsExcluded, projectInfo.FullPath);
                return ProjectInfoValidity.ExcludeFlagSet;
            }

            if (!IsProjectGuidValue(projectInfo))
            {
                logger.LogWarning(Resources.WARN_InvalidProjectGuid, projectInfo.ProjectGuid, projectInfo.FullPath);
                return ProjectInfoValidity.InvalidGuid;
            }

            if (HasDuplicateGuid(projectInfo, projects))
            {
                logger.LogWarning(Resources.WARN_DuplicateProjectGuid, projectInfo.ProjectGuid, projectInfo.FullPath);
                return ProjectInfoValidity.DuplicateGuid;
            }

            return ProjectInfoValidity.Valid;
        }

        private static bool IsProjectGuidValue(ProjectInfo project)
        {
            return project.ProjectGuid != Guid.Empty;
        }

        private static bool HasDuplicateGuid(ProjectInfo projectInfo, IEnumerable<ProjectInfo> projects)
        {
            return projects.Count(p => !p.IsExcluded && p.ProjectGuid == projectInfo.ProjectGuid) > 1;
        }

        /// <summary>
        /// Returns all of the valid files that can be analyzed. Logs warnings/info about
        /// files that cannot be analyzed.
        /// </summary>
        private static IEnumerable<string> GetFilesToAnalyze(ProjectInfo projectInfo, ILogger logger, string projectBaseDir, ProjectInfoAnalysisResult projectResult)
        {
            // We're only interested in files that exist and that are under the project root
            var result = new List<string>();
            var baseDir = projectInfo.GetProjectDirectory();
            foreach (string file in projectInfo.GetAllAnalysisFiles())
            {
                if (File.Exists(file))
                {
                    if (IsInFolder(file, baseDir))
                    {
                        result.Add(file);
                    }
                    else if (IsInFolder(file, projectBaseDir))
                    {
                        projectResult.SharedFiles.Add(file);
                    }
                    else
                    {
                        logger.LogWarning(Resources.WARN_FileIsOutsideProjectDirectory, file, projectInfo.FullPath);
                    }
                }
                else
                {
                    logger.LogWarning(Resources.WARN_FileDoesNotExist, file);
                }
            }
            return result;

        }

        private static bool IsInFolder(string filePath, string folder)
        {
            string normalizedPath = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return normalizedPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        private static string TryGetFxCopReport(ProjectInfo project, ILogger logger)
        {
            string fxCopReport = project.TryGetAnalysisFileLocation(AnalysisType.FxCop);
            if (fxCopReport != null && !File.Exists(fxCopReport))
            {
                fxCopReport = null;
                logger.LogWarning(Resources.WARN_FxCopReportNotFound, fxCopReport);
            }

            return fxCopReport;
        }

        private static string TryGetCodeCoverageReport(ProjectInfo project, ILogger logger)
        {
            string vsCoverageReport = project.TryGetAnalysisFileLocation(AnalysisType.VisualStudioCodeCoverage);
            if (vsCoverageReport != null && !File.Exists(vsCoverageReport))
            {
                vsCoverageReport = null;
                logger.LogWarning(Resources.WARN_CodeCoverageReportNotFound, vsCoverageReport);
            }
            return vsCoverageReport;
        }

        /// <summary>
        /// Returns all of the analysis properties that should
        /// be written to the sonar-project properties file
        /// </summary>
        private static AnalysisProperties GetAnalysisPropertiesToWrite(AnalysisConfig config, ILogger logger)
        {
            AnalysisProperties properties = new AnalysisProperties();

            properties.AddRange(config.GetAnalysisSettings(false).GetAllProperties()
                // Strip out any sensitive properties
                .Where(p => !p.ContainsSensitiveData()));

            // There are some properties we want to override regardless of what the user sets
            AddOrSetProperty(VSBootstrapperPropertyKey, "false", properties, logger);

            return properties;
        }

        private static void AddOrSetProperty(string key, string value, AnalysisProperties properties, ILogger logger)
        {
            Property property;
            Property.TryGetProperty(key, properties, out property);
            if (property == null)
            {
                logger.LogDebug(Resources.MSG_SettingAnalysisProperty, key, value);
                property = new Property() { Id = key, Value = value };
                properties.Add(property);
            }
            else
            {
                if (string.Equals(property.Value, value, StringComparison.InvariantCulture))
                {
                    logger.LogDebug(Resources.MSG_MandatorySettingIsCorrectlySpecified, key, value);
                }
                else
                {
                    logger.LogWarning(Resources.WARN_OverridingAnalysisProperty, key, value);
                    property.Value = value;
                }
            }
        }

        #endregion

    }
}