using System.Collections.Generic;
using System.IO;

namespace YooAsset.Editor
{
    public class DefaultSharePackRule : ISharePackRule
    {
        public void PreProcessPackShareBundle(BuildParameters buildParameters, CollectCommand command, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
            
        }

        public void ProcessingPackShareBundle(BuildParameters buildParameters, CollectCommand command, BuildAssetInfo buildAssetInfo)
        {
            PackRuleResult packRuleResult = GetShareBundleName(buildAssetInfo);
            if (packRuleResult.IsValid() == false)
                return;

            // 处理单个引用的共享资源
            if (buildAssetInfo.GetReferenceBundleCount() <= 1)
            {
                if (buildParameters.SingleReferencedPackAlone == false)
                    return;
            }

            // 设置共享资源包名
            string shareBundleName = packRuleResult.GetShareBundleName(command.PackageName, command.UniqueBundleName);
            buildAssetInfo.SetBundleName(shareBundleName);
        }

        public void PostProcessPackShareBundle(BuildParameters buildParameters, CollectCommand command, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
            
        }

        private PackRuleResult GetShareBundleName(BuildAssetInfo buildAssetInfo)
        {
            string bundleName = Path.GetDirectoryName(buildAssetInfo.AssetInfo.AssetPath);
            PackRuleResult result = new PackRuleResult(bundleName, DefaultPackRule.AssetBundleFileExtension);
            return result;
        }
    }
}
