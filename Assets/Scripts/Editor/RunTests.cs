using UnityEditor;
using UnityEngine;

public static class RunTests
{
    [MenuItem("WordDrop/Run Rules Engine Tests")]
    public static void Run()
    {
        Debug.Log("Starting Rules Engine Tests...");
        WordDrop.RulesEngineTests.RunAll();
    }
}
