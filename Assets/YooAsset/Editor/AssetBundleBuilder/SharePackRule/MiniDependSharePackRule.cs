using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace YooAsset.Editor
{
    public class MiniDependSharePackRule : ISharePackRule
    {
        const string CombineSmallMainBundlePrefix = "CombineSmallBundle_";

        /// <summary>
        /// 依赖资源仅被一个主资源引用且该主资源所在的bundle包内包含多个主资源时，是否将该依赖资源单独打包
        /// 单独打包，运行时资源加载大小最小化，bundle包数量会增加
        /// 不单独打包，bundle包数量减少，但运行时资源加载大小会增加
        /// </summary>
        public bool SingleRootAssetPackAlone = true;

        /// <summary>
        /// 是否将ShaderVariantCollection文件及其关联的shader打包到同一个bundle中
        /// </summary>
        public bool SVCShaderPackTogether = true;

        public bool ExclueSubGraphAsset = true;

        public bool CombineSmallBundles = true;
        public int SmallBundleSize = 30 * 1024;
        public int SmallBundleCombineMaxSize = 100 * 1024;
        // 是否仅合并没有依赖的小bundle
        public bool OnlyCombineNoDependencySmallBundle = false;
        //public bool CombineSmallMainBundle = false;

        HashSet<char> legalChars = new HashSet<char>()
        {
            '-', '_', '=',' ','/','\\',':','.','@','[',']','(',')'
        };

        StringBuilder sb = new StringBuilder();
        List<char> chars = new List<char>();

        HashSet<string> sceneAssets = new HashSet<string>();
        HashSet<string> svcs = new HashSet<string>();
        HashSet<string> subGraphAssets = new HashSet<string>();
        HashSet<string> mainAssets = new HashSet<string>();

        Dictionary<string, HashSet<string>> mainBundleAssets = new Dictionary<string, HashSet<string>>();

        Dictionary<string, HashSet<string>> assetChildrens = new Dictionary<string, HashSet<string>>();
        Dictionary<string, HashSet<string>> assetParents = new Dictionary<string, HashSet<string>>();
        Dictionary<string, HashSet<string>> assetDependencies = new Dictionary<string, HashSet<string>>();
        Dictionary<string, HashSet<string>> assetPacks = new Dictionary<string, HashSet<string>>();

        Dictionary<string, string> singleRootAssets = new Dictionary<string, string>();

        string GetShareBundleName(string assetPath, CollectCommand command)
        {
            var bundleName = assetPath;
            PackRuleResult result = new PackRuleResult(bundleName, DefaultPackRule.AssetBundleFileExtension);
            bundleName = result.GetShareBundleName(command.PackageName, command.UniqueBundleName);
            bool allGood = true;
            for (int i = 0; i < bundleName.Length; i++)
            {
                char c = bundleName[i];
                if (!char.IsDigit(c) && !char.IsLower(c) && !char.IsUpper(c) && !legalChars.Contains(c))
                {
                    allGood = false;
                    break;
                }
            }

            if (allGood)
                return bundleName;

            // 带有中文或者特殊字符的bundle名会导致运行时bundle创建失败
            // 除字母数字和指定字符外全部替换成 _
            chars.Clear();
            sb.Clear();
            sb.Append(bundleName);
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if (!char.IsDigit(c) && !char.IsLower(c) && !char.IsUpper(c) && !legalChars.Contains(c))
                {
                    chars.Add(c);
                    sb[i] = '_';
                }
            }
            BuildLogger.Error($"文件名 {assetPath} 包含非法字符 {string.Join(',', chars)}");
            return sb.ToString();
        }

        public void PreProcessPackShareBundle(BuildParameters buildParameters, CollectCommand command, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
            // 构建资源依赖树
            foreach (var buildAssetInfo in allBuildAssetInfos.Values)
            {
                var assetPath = buildAssetInfo.AssetInfo.AssetPath;

                if (!assetChildrens.TryGetValue(assetPath, out var childrenSet))
                {
                    childrenSet = new HashSet<string>();
                    assetChildrens[assetPath] = childrenSet;
                }

                string[] depends = command.AssetDependency.GetDependencies(assetPath, false);
                foreach (string depend in depends)
                {
                    if (depend == assetPath)
                        continue;

                    AssetInfo assetInfo = new AssetInfo(depend);
                    if (command.IgnoreRule.IsIgnore(assetInfo))
                        continue;

                    childrenSet.Add(depend);

                    if (!assetParents.TryGetValue(depend, out var parentSet))
                    {
                        parentSet = new HashSet<string>();
                        assetParents[depend] = parentSet;
                    }
                    parentSet.Add(assetPath);
                }

                var bundleName = buildAssetInfo.BundleName;
                if (!string.IsNullOrEmpty(bundleName))
                {
                    if (!mainBundleAssets.TryGetValue(bundleName, out var assetSet))
                    {
                        assetSet = new HashSet<string>();
                        mainBundleAssets[bundleName] = assetSet;
                    }
                    assetSet.Add(assetPath);

                    mainAssets.Add(assetPath);
                }

                assetPacks.Add(assetPath, new HashSet<string>() { assetPath });

                var assetType = buildAssetInfo.AssetInfo.AssetType;
                if (assetType == typeof(SceneAsset))
                {
                    sceneAssets.Add(assetPath);
                }
                else if (assetType == typeof(ShaderVariantCollection))
                {
                    svcs.Add(assetPath);
                }
                else if (assetType.Name == "SubGraphAsset")
                {
                    subGraphAssets.Add(assetPath);
                }

                var allDepends = new HashSet<string>(command.AssetDependency.GetDependencies(assetPath, true));
                allDepends.Remove(assetPath);
                assetDependencies.Add(assetPath, allDepends);
            }

            CalcSingleRootAsset();

            BuildLogger.Log($"build asset count:{allBuildAssetInfos.Count}");
            BuildLogger.Log($"main asset count:{mainAssets.Count}");
            BuildLogger.Log($"main bunlde count:{mainBundleAssets.Count}");
            //throw new Exception("test");

            HashSet<string> delChilds = new HashSet<string>();
            bool hasStrip = false;
            HashSet<string> singleBundles = new HashSet<string>();
            int loopCount = 0;
            do
            {
                loopCount++;
                // 修剪跨层级的依赖
                hasStrip = false;
                foreach (var pair in assetChildrens)
                {
                    delChilds.Clear();
                    foreach (var child in pair.Value)
                    {
                        if (delChilds.Contains(child))
                            continue;

                        if (assetDependencies.TryGetValue(child, out var dps) && dps.Overlaps(pair.Value))
                        {
                            delChilds.UnionWith(dps.Intersect(pair.Value));
                        }
                    }

                    pair.Value.ExceptWith(delChilds);
                    foreach (var delChild in delChilds)
                    {
                        if (assetParents.TryGetValue(delChild, out var parent))
                        {
                            if (parent.Remove(pair.Key))
                            {
                                BuildLogger.Log($"{loopCount} -{pair.Key} -> {delChild}");
                                hasStrip = true;
                            }
                        }
                    }
                }

                // 将只有一个父节点的节点合并到其父节点
                singleBundles.Clear();
                foreach (var pair in assetParents)
                {
                    if (pair.Value.Count == 1 && !mainAssets.Contains(pair.Key))
                    {
                        singleBundles.Add(pair.Key);
                    }
                }

                foreach (var singleBundle in singleBundles)
                {
                    var parent = assetParents[singleBundle].First();
                    BuildLogger.Log($"{loopCount} combine single bundle:{singleBundle} parent:{parent}");
                    CombineBundle(parent, singleBundle);
                }

                // 将具有相同父节点的兄弟节点合并
                Dictionary<string, string> dropSiblingBundles = new Dictionary<string, string>();
                Dictionary<string, HashSet<string>> keepSiblingBundles = new Dictionary<string, HashSet<string>>();
                foreach (var pair in assetChildrens)
                {
                    int count = pair.Value.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var child1 = pair.Value.ElementAt(i);
                        if (mainAssets.Contains(child1))
                            continue;

                        for (int j = i + 1; j < count; j++)
                        {
                            var child2 = pair.Value.ElementAt(j);
                            if (mainAssets.Contains(child2))
                                continue;

                            if (assetParents.TryGetValue(child1, out var parent1) && assetParents.TryGetValue(child2, out var parent2))
                            {
                                if (parent1.SetEquals(parent2))
                                {
                                    if (dropSiblingBundles.ContainsKey(child1) || dropSiblingBundles.ContainsKey(child2))
                                    {
                                        continue;
                                    }

                                    dropSiblingBundles.Add(child2, child1);
                                    if (!keepSiblingBundles.TryGetValue(child1, out var siblingBundles))
                                    {
                                        siblingBundles = new HashSet<string>();
                                        keepSiblingBundles[child1] = siblingBundles;
                                    }
                                    siblingBundles.Add(child2);
                                }
                            }
                        }
                    }
                }


                foreach (var pair in keepSiblingBundles)
                {
                    foreach (var child in pair.Value)
                    {
                        BuildLogger.Log($"{loopCount} combine sibling bundle:{pair.Key} child:{child}");
                        CombineBundle(pair.Key, child);
                    }
                }
            } while (hasStrip || singleBundles.Count > 0);


            // 设置bundle名称
            foreach (var pair in assetPacks)
            {
                string path = pair.Key;
                var bundleName = allBuildAssetInfos[pair.Key].BundleName;
                bool useShareBundleName = string.IsNullOrEmpty(bundleName);
                if (!useShareBundleName)
                {
                    if (sceneAssets.Contains(path))
                    {
                        foreach (var asset in pair.Value)
                        {
                            if (mainAssets.Contains(asset))
                            {
                                continue;
                            }
                            RemoveAsset(asset, allBuildAssetInfos);
                        }
                        continue;
                    }
                    if (mainBundleAssets[bundleName].Count > 1)
                    {
                    //    var mbass = mainBundleAssets[bundleName];
                    //    var assetSet = new HashSet<string>(pair.Value);
                    //    assetSet.ExceptWith(mbass);
                    //    if (!mbass.IsSubsetOf(pair.Value))
                    //    {
                    //        foreach (var asset in mbass)
                    //        {
                    //            if (!assetDependencies.TryGetValue(asset, out var dps))
                    //            {
                    //                useShareBundleName = true;
                    //                break;
                    //            }

                    //            if (!assetSet.IsSubsetOf(dps))
                    //            {
                                    useShareBundleName = true;
                    //                break;
                    //            }
                    //        }
                    //    }
                    }
                }

                if (useShareBundleName)
                {
                    bundleName = GetShareBundleName(path, command);
                }

                foreach (var asset in pair.Value)
                {
                    if (allBuildAssetInfos[asset].HasBundleName())
                        continue;

                    allBuildAssetInfos[asset].SetBundleName(bundleName);
                }
            }

            if (ExclueSubGraphAsset)
            {
                foreach (var subGraphAsset in subGraphAssets)
                {
                    if (mainAssets.Contains(subGraphAsset))
                        continue;

                    RemoveAsset(subGraphAsset, allBuildAssetInfos);
                    BuildLogger.Log($"sub graph asset:{subGraphAsset}");
                }
            }

            CombineSVCShaders(allBuildAssetInfos, command);
            CombineSingleRootAsset(allBuildAssetInfos);

            CombineSmallBundle(allBuildAssetInfos);

            BuildLogger.Log($"bundle count:{allBuildAssetInfos.Select(info => info.Value.BundleName).Distinct().Count()}");
            //throw new Exception("test");
        }


        public void ProcessingPackShareBundle(BuildParameters buildParameters, CollectCommand command, BuildAssetInfo buildAssetInfo)
        {

        }

        public void PostProcessPackShareBundle(BuildParameters buildParameters, CollectCommand command, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {

        }

        void RemoveAsset(string assetPath, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
            if (allBuildAssetInfos.TryGetValue(assetPath, out var info))
            {
                info.ClearBundleName();
                allBuildAssetInfos.Remove(assetPath);
                singleRootAssets.Remove(assetPath);
            }
        }

        void CalcSingleRootAsset()
        {
            Dictionary<string, HashSet<string>> assetAllParents = new Dictionary<string, HashSet<string>>();
            void GetParents(string assetPath, string parent)
            {
                if (assetAllParents[assetPath].Add(parent))
                {
                    if (assetParents.TryGetValue(parent, out var parentSet))
                    {
                        foreach (var p in parentSet)
                        {
                            GetParents(assetPath, p);
                        }
                    }
                }
            }
            foreach (var pair in assetParents)
            {
                if (mainAssets.Contains(pair.Key))
                {
                    continue;
                }

                if (!assetAllParents.TryGetValue(pair.Key, out var parentSet))
                {
                    parentSet = new HashSet<string>();
                    assetAllParents[pair.Key] = parentSet;
                }

                foreach (var parent in pair.Value)
                {
                    GetParents(pair.Key, parent);
                }
            }

            Dictionary<string, HashSet<string>> assetRoots = new Dictionary<string, HashSet<string>>();
            foreach (var pair in assetAllParents)
            {
                if (!assetRoots.TryGetValue(pair.Key, out var rootSet))
                {
                    rootSet = new HashSet<string>();
                    assetRoots[pair.Key] = rootSet;
                }
                foreach (var parent in pair.Value)
                {
                    if (mainAssets.Contains(parent))
                    {
                        rootSet.Add(parent);
                    }
                }
            }

            foreach (var pair in assetRoots)
            {
                if (pair.Value.Count == 1)
                {
                    singleRootAssets.Add(pair.Key, pair.Value.First());
                }
            }
        }

        void CombineSingleRootAsset(Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
            var singleRootBundleCount = singleRootAssets
                .Where(a => !mainBundleAssets.ContainsKey(allBuildAssetInfos[a.Key].BundleName))
                .Select(a => allBuildAssetInfos[a.Key].BundleName)
                .Distinct()
                .Count();
            BuildLogger.Log($"single root share bundle count:{singleRootBundleCount}");

            if (SingleRootAssetPackAlone)
                return;

            foreach (var pair in singleRootAssets)
            {
                var info = allBuildAssetInfos[pair.Key];
                var rootBundleName = allBuildAssetInfos[pair.Value].BundleName;
                if (info.BundleName != rootBundleName)
                {
                    var assets = mainBundleAssets[rootBundleName];
                    //if (assets.Count > 1)
                    //    continue;

                    var asset = assets.First();
                    if (sceneAssets.Contains(asset))
                        continue;

                    Debug.Log($"single root asset:{pair.Key} bundle:{info.BundleName} parent:{pair.Value} bundle:{rootBundleName}");

                    info.ClearBundleName();
                    info.SetBundleName(rootBundleName);
                }
            }
        }

        void CombineSVCShaders(Dictionary<string, BuildAssetInfo> allBuildAssetInfos, CollectCommand command)
        {
            if (!SVCShaderPackTogether)
                return;

            foreach (var svc in svcs)
            {
                var bundleName = allBuildAssetInfos[svc].BundleName;
                string[] depends = command.AssetDependency.GetDependencies(svc, false);
                foreach (string depend in depends)
                {
                    if (depend == svc)
                        continue;

                    if (allBuildAssetInfos.TryGetValue(depend, out var info))
                    {
                        info.ClearBundleName();
                        info.SetBundleName(bundleName);
                    }
                }
            }

        }

        void CombineBundle(string keepBundle, string dropBundle)
        {
            if (!assetParents.TryGetValue(keepBundle, out var keepBundleParents))
            {
                keepBundleParents = new HashSet<string>();
                assetParents[keepBundle] = keepBundleParents;
            }

            if (assetParents.TryGetValue(dropBundle, out var dropBundleParents))
            {
                assetParents.Remove(dropBundle);

                keepBundleParents.UnionWith(dropBundleParents);

                foreach (var dropBundleParent in dropBundleParents)
                {
                    if (dropBundleParent == keepBundle)
                        continue;

                    if (assetChildrens.TryGetValue(dropBundleParent, out var childrens))
                    {
                        childrens.Remove(dropBundle);
                        childrens.Add(keepBundle);
                    }
                }
            }

            keepBundleParents.Remove(dropBundle);
            keepBundleParents.Remove(keepBundle);

            if (!assetChildrens.TryGetValue(keepBundle, out var keepBundleChildrens))
            {
                keepBundleChildrens = new HashSet<string>();
                assetChildrens[keepBundle] = keepBundleChildrens;
            }

            if (assetChildrens.TryGetValue(dropBundle, out var dropBundleChildrens))
            {
                assetChildrens.Remove(dropBundle);
                keepBundleChildrens.UnionWith(dropBundleChildrens);

                foreach (var childBundle in dropBundleChildrens)
                {
                    if (childBundle == keepBundle)
                        continue;

                    if (assetParents.TryGetValue(childBundle, out var parents))
                    {
                        parents.Remove(dropBundle);
                        parents.Add(keepBundle);
                    }
                }
            }

            keepBundleChildrens.Remove(dropBundle);
            keepBundleChildrens.Remove(keepBundle);

            if (assetPacks.TryGetValue(dropBundle, out var dropBundleAssets))
            {
                assetPacks.Remove(dropBundle);
                assetPacks[keepBundle].UnionWith(dropBundleAssets);
            }
        }

        MethodInfo getStorageMemorySizeLongMethod = null;
        long GetTextureFileSize(Texture2D texture)
        {
            if (getStorageMemorySizeLongMethod == null)
            {
                Type textureUtilType = typeof(TextureImporter).Assembly.GetType("UnityEditor.TextureUtil");
                getStorageMemorySizeLongMethod = textureUtilType.GetMethod("GetStorageMemorySizeLong", BindingFlags.Static | BindingFlags.Public);
            }

            if (getStorageMemorySizeLongMethod == null)
            {
                BuildLogger.Error("GetStorageMemorySizeLong method not found in UnityEditor.TextureUtil");
                return 0;
            }
            return (long)getStorageMemorySizeLongMethod.Invoke(null, new object[] { texture });
        }


        void CombineSmallBundle(Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
            if(!CombineSmallBundles)
                return;

            Dictionary<string, HashSet<string>> bundleAssets = new Dictionary<string, HashSet<string>>();
            foreach (var pair in allBuildAssetInfos)
            {
                var assetPath = pair.Key;
                var info = pair.Value;
                if (!info.HasBundleName())
                    continue;

                if (!bundleAssets.TryGetValue(info.BundleName, out var assets))
                {
                    assets = new HashSet<string>();
                    bundleAssets[info.BundleName] = assets;
                }
                assets.Add(assetPath);
            }

            Dictionary<string, long> bundleSizes = new Dictionary<string, long>();
            Dictionary<string, long> assetSizes = new Dictionary<string, long>();
            foreach (var pair in bundleAssets)
            {
                var bundleName = pair.Key;
                //if (mainBundleAssets.ContainsKey(bundleName))
                //    continue;

                var assets = pair.Value;
                long totalSize = 0;
                bool b = false;
                foreach (var asset in assets)
                {
                    if (sceneAssets.Contains(asset))
                    {
                        b = true;
                        break;
                    }

                    if(OnlyCombineNoDependencySmallBundle)
                    {
                        if (assetDependencies.TryGetValue(asset, out var dps) && dps.Count > 0)
                        {
                            if (!dps.IsSubsetOf(assets))
                            {
                                b = true;
                                break;
                            }
                        }
                    }

                    var assetType = allBuildAssetInfos[asset].AssetInfo.AssetType;
                    long size = 0;
                    if(assetType == typeof(Texture2D))
                    {
                        size = GetTextureFileSize(AssetDatabase.LoadAssetAtPath<Texture2D>(asset));
                    }
                    else
                    {
                        size = Profiler.GetRuntimeMemorySizeLong(AssetDatabase.LoadAssetAtPath(asset, assetType));
                        var fileSize = new System.IO.FileInfo(asset).Length;
                        if(fileSize > size)
                        {
                            size = fileSize;
                        }
                    }
                    totalSize += size;
                    assetSizes[asset] = size;
                }

                if (b)
                    continue;

                bundleSizes[bundleName] = totalSize;
            }

            var smallShareBundles = bundleSizes
                .Where(pair=> mainBundleAssets.ContainsKey(pair.Key) == false)
                .Where(pair => pair.Value < SmallBundleSize)
                .Select(pair => pair.Key)
                .ToList();

            if(smallShareBundles.Count == 0)
            {
                BuildLogger.Log("No small bundles to combine.");
                return;
            }

            //smallShareBundles.Sort((a, b) =>
            //{
            //    return bundleSizes[a].CompareTo(bundleSizes[b]);
            //});
            CombineSmallBundle(smallShareBundles);


            void CombineSmallBundle(List<string> smallBundles)
            {
                for (int i = 0; i < smallBundles.Count; i++)
                {
                    var bundleName = smallBundles[i];
                    long size = bundleSizes[bundleName];
                    bundleName = "combine_" + bundleName;
                    var assets = bundleAssets[smallBundles[i]];
                    foreach (var asset in assets)
                    {
                        allBuildAssetInfos[asset].ClearBundleName();
                        allBuildAssetInfos[asset].SetBundleName(bundleName);
                        size += assetSizes[asset];
                    }
                    while (size < SmallBundleCombineMaxSize && i < smallBundles.Count - 1)
                    {
                        i++;
                        assets = bundleAssets[smallBundles[i]];
                        foreach (var asset in assets)
                        {
                            allBuildAssetInfos[asset].ClearBundleName();
                            allBuildAssetInfos[asset].SetBundleName(bundleName);
                            size += assetSizes[asset];
                        }
                    }
                }
            }

            Dictionary<string, HashSet<string>> combineMainBundles = new Dictionary<string, HashSet<string>>();
            foreach (var pair in allBuildAssetInfos)
            {
                var info = pair.Value;
                if (!info.HasBundleName())
                    continue;

                var bundleName = info.BundleName;
                if(!mainBundleAssets.ContainsKey(bundleName))
                    continue;

                for (int i = info.AssetTags.Count - 1; i >= 0; i--)
                {
                    string tag = info.AssetTags[i];
                    if (tag.StartsWith(CombineSmallMainBundlePrefix))
                    {
                        if (!combineMainBundles.TryGetValue(tag, out var bundles))
                        {
                            bundles = new HashSet<string>();
                            combineMainBundles[tag] = bundles;
                        }
                        bundles.Add(bundleName);

                        info.AssetTags.RemoveAt(i);
                        break;
                    }
                }
            }

            foreach(var pair in combineMainBundles)
            {
                var smallMainBundles = pair.Value
                    .Where(bundleName => bundleSizes.ContainsKey(bundleName))
                    .Where(bundleName => bundleSizes[bundleName] < SmallBundleSize)
                    .ToList();

                CombineSmallBundle(smallMainBundles);
            }
        }
    }
}
