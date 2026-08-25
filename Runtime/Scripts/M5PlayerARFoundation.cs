using Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Module5.DI;
namespace Module5.Player.ARFoundation
{
    public class M5PlayerARFoundation : LifetimeScope
    {
        public CreateObjectManagerData createObjectManagerData;
        public SceneManagerData sceneManagerData;
        public ARFoundationAnchorTrackerData arFoundationAnchorTrackerData;

        public ProgressBarManager progressBarManager;
        public FadeController fadeController;

        // 1. 設定依賴關係 (Awake 階段執行)
        protected override void Configure(IContainerBuilder builder)
        {
            if (fadeController != null)
                builder.RegisterComponent(fadeController).AsImplementedInterfaces();
            if (progressBarManager != null)
                builder.RegisterComponent(progressBarManager).AsImplementedInterfaces();

            builder.Register<AnchorRegistry>(Lifetime.Singleton)
            .As<IAnchorRegistry>();

            builder.Register<PersistentToAnchorDataConverterCache>(Lifetime.Singleton)
            .As<IPersistentToAnchorDataConverterCache>();

            builder.Register<PersistentToGameObjectConverterRegistry>(Lifetime.Singleton)
            .As<IPersistentToGameObjectConverterRegistry>();

            // 註冊持有的實例
            builder.Register<ProjectPathState>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<ProjectManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<ProjectAssetManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<AssetDataBaseManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<AssetLoaderManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<AnchorManager>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.RegisterInstance(arFoundationAnchorTrackerData);
            builder.Register<ARFoundationAnchorTracker>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.RegisterInstance(sceneManagerData); // Register Data
            builder.Register<SceneManager>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.Register<AssetMetaManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<AssetImportPipeline>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<AssetStorageManager>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.RegisterInstance(createObjectManagerData); // Register Data
            builder.Register<CreateObjectManager>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.Register<VariableTypeRegistry>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<VariableManager>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.Register<CommandRegistry>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<ConditionRegistry>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.Register<EventManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<UnityActionExecutor>(Lifetime.Singleton);
            builder.Register<TweenManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<TimerManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<ObjectInteractionService>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<ConsoleManager>(Lifetime.Singleton).AsImplementedInterfaces();
            builder.Register<PlayerInteractionInputProvider>(Lifetime.Singleton).AsImplementedInterfaces();

            builder.Register<PngStorage>(Lifetime.Singleton);
            builder.Register<AudioStorage>(Lifetime.Singleton);
            builder.Register<GltfStorage>(Lifetime.Singleton);
            builder.Register<SceneStorage>(Lifetime.Singleton);
            builder.Register<PrefabStorage>(Lifetime.Singleton);
            builder.Register<UnknownAssetStorage>(Lifetime.Singleton);

            // 處理注入與初始化 (確保在 Start 之前完成)
            builder.RegisterBuildCallback(container =>
            {

                IAssetStorageManager assetStorageManager = container.Resolve<IAssetStorageManager>();
                assetStorageManager.RegisterStorage(container.Resolve<PngStorage>());
                assetStorageManager.RegisterStorage(container.Resolve<AudioStorage>());
                assetStorageManager.RegisterStorage(container.Resolve<GltfStorage>());
                assetStorageManager.RegisterStorage(container.Resolve<SceneStorage>());
                assetStorageManager.RegisterStorage(container.Resolve<PrefabStorage>());

                DefaultVariableRegistration.RegisterDefaultVariableTypes(container.Resolve<IVariableTypeRegistry>());
                DefaultEventRegistration.RegisterDefaultConditions(container.Resolve<IConditionRegistry>());
                DefaultEventRegistration.RegisterDefaultCommands(container.Resolve<ICommandRegistry>(), container);
                DefaultConverterRegistration.RegisterDefaultConverters(container.Resolve<IPersistentToGameObjectConverterRegistry>());
                DefaultAnchorRegistration.RegisterDefaultAnchors(container.Resolve<IAnchorRegistry>());

                IEventManager eventManager = container.Resolve<IEventManager>();
                eventManager.Initialize(this,
                    container.Resolve<IAssetDataBaseManager>(),
                    container.Resolve<IVariableManager>(),
                    container.Resolve<IAssetLoaderManager>(),
                    container.Resolve<UnityActionExecutor>(),
                    container.Resolve<IConditionRegistry>());

                JsonSerializerSettings settings = JsonConvert.DefaultSettings != null
                    ? JsonConvert.DefaultSettings()
                    : new JsonSerializerSettings();
                settings.Converters.Insert(0, new PersistentGameObjectConverter(container.Resolve<IPersistentToGameObjectConverterRegistry>()));
                settings.Converters.Insert(0, new PersistentAnchorConverter(container.Resolve<IAnchorRegistry>()));
                settings.Converters.Insert(0, new VariableConverter(container.Resolve<IVariableTypeRegistry>()));
                JsonConvert.DefaultSettings = () => settings;
            });
        }
    }
}