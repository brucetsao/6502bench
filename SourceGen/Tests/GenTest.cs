﻿/*
 * Copyright 2018 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;

using CommonUtil;
using SourceGen.AsmGen;

namespace SourceGen.Tests {
    /// <summary>
    /// Source code generation regression test.
    /// 
    /// The generator is tested in two ways: (1) by comparing the output to known-good
    /// sources, and (2) by running it through the assembler.  Assembling the sources is
    /// important to ensure that we don't get bad sources in the "known-good" set.
    /// 
    /// This does not take assembler version into account, so it will not be helpful for
    /// monitoring compatibility with old versions of assemblers.
    /// </summary>
    public class GenTest {
        private const string TEST_DIR_NAME = "SGTestData";
        private const string EXPECTED_DIR_NAME = "Expected";

        //private static char[] sInvalidChars = new char[] { '.', '_' };
        private const string TestCasePattern = @"^\d\d\d\d-[A-Za-z0-9-]+$";
        private static Regex sTestCaseRegex = new Regex(TestCasePattern);

        /// <summary>
        /// Whitelist of removable names for ScrubWorkDirectory().
        /// </summary>
        private static string[] sScrubList = new string[] {
            "_FileInformation.txt",             // created by Merlin 32
            "error_output.txt",                 // created by Merlin 32 (only when errors found)
        };

        /// <summary>
        /// Test result.  One of these will be created for every {test-case, assembler} pair.
        /// </summary>
        public class GenTestResults {
            public string PathName { get; private set; }
            public string FileName { get; private set; }
            public AssemblerInfo.Id AsmId { get; private set; }

            public FileLoadReport ProjectLoadReport { get; set; }

            public bool GenerateOkay { get; set; }
            public bool AssembleOkay { get; set; }
            public AssemblerResults AsmResults { get; set; }    // may be null

            public TaskTimer Timer { get; set; }                // may be null

            public GenTestResults(string pathName, AssemblerInfo.Id asmId) {
                PathName = pathName;
                AsmId = asmId;

                FileName = Path.GetFileName(pathName);
            }

            // Return a string for use in the UI combo box.
            public override string ToString() {
                return (GenerateOkay && AssembleOkay ? "OK" : "FAIL") + " - " +
                    FileName + " - " + AsmId.ToString();
            }
        }

        /// <summary>
        /// If true, don't scrub directories.
        /// </summary>
        public bool RetainOutput { get; set; }

        /// <summary>
        /// Directory with test cases.
        /// </summary>
        private string mTestDir;

        private BackgroundWorker mWorker;

        private List<GenTestResults> mResults = new List<GenTestResults>();


        /// <summary>
        /// Runs generate/assemble test cases.  Main entry point.
        /// </summary>
        /// <param name="worker">Background worker object from dialog box.</param>
        public List<GenTestResults> Run(BackgroundWorker worker) {
            Debug.Assert(mWorker == null);  // don't re-use object

            mWorker = worker;
            string runtimeDir = RuntimeDataAccess.GetDirectory();
            mTestDir = Path.Combine(Path.GetDirectoryName(runtimeDir), TEST_DIR_NAME);

            if (!Directory.Exists(mTestDir)) {
                ReportErrMsg("Regression test directory not found: " + mTestDir);
                ReportFailure();
                return null;
            }

            List<string> testCases = new List<string>();
            foreach (string pathName in Directory.EnumerateFiles(mTestDir)) {
                // Filter out everything that doesn't look like "1000-nifty-test".  We
                // want to ignore .dis65 files and assembler output (which has the name
                // of the assembler following an underscore).
                string fileName = Path.GetFileName(pathName);
                MatchCollection matches = sTestCaseRegex.Matches(fileName);
                if (matches.Count == 0) {
                    //ReportProgress("Ignoring " + fileName + "\r\n", Color.Gray);
                    continue;
                }

                ReportProgress("Found " + fileName + "\r\n");
                testCases.Add(pathName);
            }

            ReportProgress("Processing " + testCases.Count + " test cases...\r\n");
            DateTime startWhen = DateTime.Now;

            int successCount = 0;
            foreach (string pathName in testCases) {
                if (GenerateAndAssemble(pathName)) {
                    successCount++;
                }

                if (worker.CancellationPending) {
                    ReportProgress("\r\nCancelled.\r\n", Color.Red);
                    return mResults;
                }
            }

            DateTime endWhen = DateTime.Now;

            if (successCount == testCases.Count) {
                ReportProgress(string.Format("All " + testCases.Count +
                    " tests passed in {0:N3} sec\r\n",
                    (endWhen - startWhen).TotalSeconds), Color.Green);
            } else {
                ReportProgress(successCount + " of " + testCases.Count + " tests passed\r\n");
            }

            return mResults;
        }

        private void ReportProgress(string msg) {
            mWorker.ReportProgress(0, new ProgressMessage(msg));
        }

        private void ReportProgress(string msg, Color color) {
            mWorker.ReportProgress(0, new ProgressMessage(msg, color));
        }

        private void ReportErrMsg(string msg) {
            ReportProgress(" [" + msg + "] ", Color.Blue);
        }

        private void ReportSuccess() {
            ReportProgress(" success\r\n", Color.Green);
        }

        private void ReportFailure() {
            ReportProgress(" failed\r\n", Color.Red);
        }

        /// <summary>
        /// Extracts the test's number from the pathname.
        /// </summary>
        /// <param name="pathName">Full or partial path to test file.</param>
        /// <returns>Test number.</returns>
        private int GetTestNum(string pathName) {
            // Should always succeed if pathName matched on our regex.
            string fileName = Path.GetFileName(pathName);
            return int.Parse(fileName.Substring(0, 4));
        }

        /// <summary>
        /// Generates source code for the specified test case, assembles it, and compares
        /// the output of both steps to expected values.  The process is repeated for every
        /// known assembler.
        /// 
        /// If an assembler is known but not configured, the assembly step is skipped, and
        /// does not count as a failure.
        /// </summary>
        /// <param name="pathName">Full path to test case.</param>
        /// <returns>True if all assemblers worked as expected.</returns>
        private bool GenerateAndAssemble(string pathName) {
            ReportProgress(Path.GetFileName(pathName) + "...\r\n");

            // Create DisasmProject object, either as a new project for a plain data file,
            // or from a project file.
            DisasmProject project = InstantiateProject(pathName,
                out FileLoadReport projectLoadReport);
            if (project == null) {
                ReportFailure();
                return false;
            }

            int testNum = GetTestNum(pathName);

            // Create a temporary directory to work in.
            string workDir = CreateWorkDirectory(pathName);
            if (string.IsNullOrEmpty(workDir)) {
                ReportFailure();
                project.Cleanup();
                return false;
            }

            AppSettings settings = CreateNormalizedSettings();
            ApplyProjectSettings(settings, project);

            // Iterate through all known assemblers.
            bool didFail = false;
            foreach (AssemblerInfo.Id asmId in
                    (AssemblerInfo.Id[])Enum.GetValues(typeof(AssemblerInfo.Id))) {
                if (asmId == AssemblerInfo.Id.Unknown) {
                    continue;
                }

                string fileName = Path.GetFileName(pathName);
                TaskTimer timer = new TaskTimer();
                timer.StartTask("Full Test Duration");

                // Create results object and add it to the list.  We'll add stuff to it for
                // as far as we get.
                GenTestResults results = new GenTestResults(pathName, asmId);
                mResults.Add(results);
                results.ProjectLoadReport = projectLoadReport;

                // Generate source code.
                ReportProgress("  " + asmId.ToString() + " generate...");
                IGenerator gen = AssemblerInfo.GetGenerator(asmId);
                if (gen == null) {
                    ReportErrMsg("generator unavailable");
                    ReportProgress("\r\n");
                    //didFail = true;
                    continue;
                }
                timer.StartTask("Generate Source");
                gen.Configure(project, workDir, fileName,
                    AssemblerVersionCache.GetVersion(asmId), settings);
                List<string> genPathNames = gen.GenerateSource(mWorker);
                timer.EndTask("Generate Source");
                if (mWorker.CancellationPending) {
                    // The generator will stop early if a cancellation is requested.  If we
                    // don't break here, the compare function will report a failure, which
                    // isn't too problematic but looks funny.
                    break;
                }

                ReportProgress(" verify...");
                timer.StartTask("Compare Source to Expected");
                bool match = CompareGeneratedToExpected(pathName, genPathNames);
                timer.EndTask("Compare Source to Expected");
                if (match) {
                    ReportSuccess();
                    results.GenerateOkay = true;
                } else {
                    ReportFailure();
                    didFail = true;

                    // The fact that it doesn't match the expected sources doesn't mean it's
                    // invalid.  Go ahead and try to build it.
                    //continue;
                }

                // Assemble code.
                ReportProgress("  " + asmId.ToString() + " assemble...");
                IAssembler asm = AssemblerInfo.GetAssembler(asmId);
                if (asm == null) {
                    ReportErrMsg("assembler unavailable");
                    ReportProgress("\r\n");
                    continue;
                }

                timer.StartTask("Assemble Source");
                asm.Configure(genPathNames, workDir);
                AssemblerResults asmResults = asm.RunAssembler(mWorker);
                timer.EndTask("Assemble Source");
                if (asmResults == null) {
                    ReportErrMsg("unable to run assembler");
                    ReportFailure();
                    didFail = true;
                    continue;
                }
                if (asmResults.ExitCode != 0) {
                    ReportErrMsg("assembler returned code=" + asmResults.ExitCode);
                    ReportFailure();
                    didFail = true;
                    continue;
                }

                results.AsmResults = asmResults;

                ReportProgress(" verify...");
                timer.StartTask("Compare Binary to Expected");
                FileInfo fi = new FileInfo(asmResults.OutputPathName);
                if (fi.Length != project.FileData.Length) {
                    ReportErrMsg("asm output mismatch: length is " + fi.Length + ", expected " +
                        project.FileData.Length);
                    ReportFailure();
                    didFail = true;
                    continue;
                } else if (!FileUtil.CompareBinaryFile(project.FileData, asmResults.OutputPathName,
                        out int badOffset, out byte badFileVal)) {
                    ReportErrMsg("asm output mismatch: offset +" + badOffset.ToString("x6") +
                        " has value $" + badFileVal.ToString("x2") + ", expected $" +
                        project.FileData[badOffset].ToString("x2"));
                    ReportFailure();
                    didFail = true;
                    continue;
                }
                timer.EndTask("Compare Binary to Expected");

                // Victory!
                results.AssembleOkay = true;
                ReportSuccess();

                timer.EndTask("Full Test Duration");
                results.Timer = timer;

                // We don't scrub the directory on success at this point.  We could, but we'd
                // need to remove only those files associated with the currently assembler.
                // Otherwise, a failure followed by a success would wipe out the unsuccessful
                // temporaries.
            }

            // If something failed, leave the bits around for examination.  Otherwise, try to
            // remove the directory and all its contents.
            if (!didFail && !RetainOutput) {
                ScrubWorkDirectory(workDir, testNum);
                RemoveWorkDirectory(workDir);
            }

            project.Cleanup();
            return !didFail;
        }

        /// <summary>
        /// Gets a copy of the AppSettings with a standard set of formatting options (e.g. lower
        /// case for everything).
        /// </summary>
        /// <returns>New app settings object.</returns>
        private AppSettings CreateNormalizedSettings() {
            AppSettings settings = AppSettings.Global.GetCopy();

            // Override all asm formatting options.  We can ignore ShiftBeforeAdjust and the
            // pseudo-op names because those are set by the generators.
            settings.SetBool(AppSettings.FMT_UPPER_HEX_DIGITS, false);
            settings.SetBool(AppSettings.FMT_UPPER_OP_MNEMONIC, false);
            settings.SetBool(AppSettings.FMT_UPPER_PSEUDO_OP_MNEMONIC, false);
            settings.SetBool(AppSettings.FMT_UPPER_OPERAND_A, true);
            settings.SetBool(AppSettings.FMT_UPPER_OPERAND_S, true);
            settings.SetBool(AppSettings.FMT_UPPER_OPERAND_XY, false);
            settings.SetBool(AppSettings.FMT_ADD_SPACE_FULL_COMMENT, false);

            // Don't show the assembler ident line.  You can make a case for this being
            // mandatory, since the generated code is only guaranteed to work with the
            // assembler for which it was targeted, but I expect we'll quickly get to a
            // place where we don't have to work around assembler bugs, and this will just
            // become a nuisance.
            settings.SetBool(AppSettings.SRCGEN_ADD_IDENT_COMMENT, false);

            // Don't break lines with long labels.  That way we can redefine "long"
            // without breaking our tests.  (This is purely cosmetic.)
            settings.SetBool(AppSettings.SRCGEN_LONG_LABEL_NEW_LINE, false);

            // This could be on or off.  Off seems less distracting.
            settings.SetBool(AppSettings.SRCGEN_SHOW_CYCLE_COUNTS, false);

            // Disable label localization.  We want to be able to play with this a bit
            // without disrupting all the other tests.  Use a test-only feature to enable
            // it for the localization test.
            settings.SetBool(AppSettings.SRCGEN_DISABLE_LABEL_LOCALIZATION, true);

            IEnumerator<AssemblerInfo> iter = AssemblerInfo.GetInfoEnumerator();
            while (iter.MoveNext()) {
                AssemblerInfo.Id asmId = iter.Current.AssemblerId;
                AssemblerConfig curConfig =
                    AssemblerConfig.GetConfig(settings, asmId);
                AssemblerConfig defConfig =
                    AssemblerInfo.GetAssembler(asmId).GetDefaultConfig();

                // Merge the two together.  We want the default assembler config for most
                // things, but the executable path from the current config.
                defConfig.ExecutablePath = curConfig.ExecutablePath;

                // Write it into the test settings.
                AssemblerConfig.SetConfig(settings, asmId, defConfig);
            }

            return settings;
        }

        /// <summary>
        /// Applies app setting overrides that were specified in the project settings.
        /// </summary>
        private void ApplyProjectSettings(AppSettings settings, DisasmProject project) {
            // We could probably make this a more general mechanism, but that would strain
            // things a bit, since we need to know the settings name, bool/int/string, and
            // desired value.  Easier to just have a set of named features.
            const string ENABLE_LABEL_LOCALIZATION = "__ENABLE_LABEL_LOCALIZATION";
            const string ENABLE_LABEL_NEWLINE = "__ENABLE_LABEL_NEWLINE";

            if (project.ProjectProps.ProjectSyms.ContainsKey(ENABLE_LABEL_LOCALIZATION)) {
                settings.SetBool(AppSettings.SRCGEN_DISABLE_LABEL_LOCALIZATION, false);
            }
            if (project.ProjectProps.ProjectSyms.ContainsKey(ENABLE_LABEL_NEWLINE)) {
                settings.SetBool(AppSettings.SRCGEN_LONG_LABEL_NEW_LINE, true);
            }
        }

        private DisasmProject InstantiateProject(string dataPathName,
                out FileLoadReport projectLoadReport) {
            DisasmProject project = new DisasmProject();
            // always use AppDomain sandbox

            projectLoadReport = null;

            int testNum = GetTestNum(dataPathName);

            if (testNum < 2000) {
                // create new disasm project for data file
                byte[] fileData;
                try {
                    fileData = LoadDataFile(dataPathName);
                } catch (Exception ex) {
                    ReportErrMsg(ex.Message);
                    return null;
                }

                project.Initialize(fileData.Length);
                project.PrepForNew(fileData, Path.GetFileName(dataPathName));
                // no platform symbols to load
            } else {
                // deserialize project file, failing if we can't find it
                string projectPathName = dataPathName + ProjectFile.FILENAME_EXT;
                if (!ProjectFile.DeserializeFromFile(projectPathName,
                        project, out projectLoadReport)) {
                    ReportErrMsg(projectLoadReport.Format());
                    return null;
                }

                byte[] fileData;
                try {
                    fileData = LoadDataFile(dataPathName);
                } catch (Exception ex) {
                    ReportErrMsg(ex.Message);
                    return null;
                }

                project.SetFileData(fileData, Path.GetFileName(dataPathName));
                project.ProjectPathName = projectPathName;
                project.LoadExternalFiles();
            }

            TaskTimer genTimer = new TaskTimer();
            DebugLog genLog = new DebugLog();
            genLog.SetMinPriority(DebugLog.Priority.Silent);
            project.Analyze(UndoableChange.ReanalysisScope.CodeAndData, genLog, genTimer);

            return project;
        }

        /// <summary>
        /// Loads the test case data file.
        /// 
        /// Throws an exception on failure.
        /// </summary>
        /// <param name="pathName">Full path to test case data file.</param>
        /// <returns>File contents.</returns>
        private byte[] LoadDataFile(string pathName) {
            byte[] fileData;

            using (FileStream fs = File.Open(pathName, FileMode.Open, FileAccess.Read)) {
                Debug.Assert(fs.Length <= DisasmProject.MAX_DATA_FILE_SIZE);
                fileData = new byte[fs.Length];
                int actual = fs.Read(fileData, 0, (int)fs.Length);
                if (actual != fs.Length) {
                    // Not expected -- should be able to read the entire file in one shot.
                    throw new Exception(Properties.Resources.OPEN_DATA_PARTIAL_READ);
                }
            }
            return fileData;
        }

        /// <summary>
        /// Creates a work directory for the specified test case.  The new directory will be
        /// created in the same directory as the test, and named after it.
        /// 
        /// If the directory already exists, the previous contents will be scrubbed.
        /// 
        /// If the file already exists but isn't a directory, this will fail.
        /// </summary>
        /// <param name="pathName">Test case path name.</param>
        /// <returns>Path of work directory, or null if creation failed.</returns>
        private string CreateWorkDirectory(string pathName) {
            string baseDir = Path.GetDirectoryName(pathName);
            int testNum = GetTestNum(pathName);
            string workDirName = "tmp" + testNum.ToString();
            string workDirPath = Path.Combine(baseDir, workDirName);
            if (Directory.Exists(workDirPath)) {
                ScrubWorkDirectory(workDirPath, testNum);
            } else if (File.Exists(workDirPath)) {
                ReportErrMsg("file '" + workDirPath + "' exists, not directory");
                return null;
            } else {
                try {
                    Directory.CreateDirectory(workDirPath);
                } catch (Exception ex) {
                    ReportErrMsg(ex.Message);
                    return null;
                }
            }
            return workDirPath;
        }

        /// <summary>
        /// Removes the contents of a temporary work directory.  Only files that we believe
        /// to be products of the generator or assembler are removed.
        /// </summary>
        /// <param name="workDir"></param>
        /// <param name="testNum"></param>
        private void ScrubWorkDirectory(string workDir, int testNum) {
            string checkString = testNum.ToString();
            if (checkString.Length != 4) {
                Debug.Assert(false);
                return;
            }

            foreach (string pathName in Directory.EnumerateFiles(workDir)) {
                bool doRemove = false;
                string fileName = Path.GetFileName(pathName);
                if (fileName.Contains(checkString)) {
                    doRemove = true;
                } else {
                    foreach (string str in sScrubList) {
                        if (fileName == str) {
                            doRemove = true;
                        }
                    }
                }

                if (!doRemove) {
                    ReportErrMsg("not removing '" + fileName + "'");
                    continue;
                } else {
                    try {
                        File.Delete(pathName);
                        //Debug.WriteLine("removed " + pathName);
                    } catch (Exception ex) {
                        ReportErrMsg("unable to remove '" + fileName + "': " + ex.Message);
                        // don't stop -- keep trying to remove things
                    }
                }
            }
        }

        private void RemoveWorkDirectory(string workDir) {
            try {
                Directory.Delete(workDir);
            } catch (Exception ex) {
                ReportErrMsg("unable to remove work dir: " + ex.Message);
            }
        }

        /// <summary>
        /// Compares each file in genFileNames to the corresponding file in Expected.
        /// </summary>
        /// <param name="pathName">Full pathname of test case.</param>
        /// <param name="genPathNames">List of file names from source generator.</param>
        /// <returns></returns>
        private bool CompareGeneratedToExpected(string pathName, List<string> genPathNames) {
            string expectedDir = Path.Combine(Path.GetDirectoryName(pathName), EXPECTED_DIR_NAME);

            foreach (string path in genPathNames) {
                string fileName = Path.GetFileName(path);
                string compareName = Path.Combine(expectedDir, fileName);

                if (!File.Exists(compareName)) {
                    // File was generated unexpectedly.
                    ReportErrMsg("file '" + fileName + "' not found in " + EXPECTED_DIR_NAME);
                    return false;
                }

                // Compare the file contents as lines of text.  The files may use different
                // line terminators (e.g. LF vs. CRLF), so we can't use file length as a
                // factor.
                if (!FileUtil.CompareTextFiles(path, compareName, out int firstDiffLine,
                        out string line1, out string line2)) {
                    ReportErrMsg("file '" + fileName + "' differs on line " + firstDiffLine);
                    Debug.WriteLine("File #1: " + line1);
                    Debug.WriteLine("File #2: " + line2);
                    return false;
                }
            }

            // NOTE: to be thorough, we should check to see if a file exists in Expected
            // that doesn't exist in the work directory.  This is slightly more awkward since
            // Expected is a big pile of everything, but we should be able to do it by
            // filtering filenames with the test number.

            return true;
        }
    }
}
