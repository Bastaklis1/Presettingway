// Presetway ReShade addon
//
// Subscribes to the "Presetway" Sharingway provider (published by the Presetway
// Dalamud plugin) and calls effect_runtime::set_current_preset_path() whenever
// a new preset path arrives.
//
// ARCHITECTURE NOTE (v2): the first version applied the preset switch from
// inside the reshade_present addon-event callback, on the theory that
// "somewhere in ReShade's per-frame dispatch" was the correct place to touch
// the effect_runtime API. That crashed reliably and reproducibly with
// STATUS_STACK_OVERFLOW (0xc00000fd), confirmed via Windows Event Viewer
// across multiple independent sessions -- not a data race, not corruption,
// an actual call-stack overflow. That's consistent with set_current_preset_path
// re-entering ReShade's own event dispatch while already nested inside it,
// on the same thread, deep enough to eventually exceed the stack.
//
// This version applies the switch from a dedicated worker thread instead,
// entirely outside any ReShade event-dispatch call stack, with a large
// explicit stack size as additional insurance. It no longer needs a
// per-frame hook at all -- only init_effect_runtime/destroy_effect_runtime,
// to know when a valid effect_runtime* exists to call into.

#include <reshade.hpp>
#include <atomic>
#include <mutex>
#include <condition_variable>
#include <string>
#include <optional>
#include <process.h> // _beginthreadex

#include "ThirdParty/Sharingway/Sharingway.h"

using namespace reshade::api;

namespace
{
    Sharingway::Subscriber* g_subscriber = nullptr;
    std::atomic<bool> g_subscribedToPresetway{ false };

    // The currently-valid effect_runtime, captured/cleared via the two
    // lifecycle events below. Guarded separately from the queue mutex since
    // it's written from the render thread but read from the worker thread.
    std::mutex g_runtimeMutex;
    effect_runtime* g_runtime = nullptr;

    // Pending-switch queue, now consumed by a dedicated worker thread instead
    // of a ReShade event callback.
    std::mutex g_queueMutex;
    std::condition_variable g_queueCv;
    std::optional<std::string> g_pendingPresetPath;
    std::string g_lastAppliedPresetPath;
    bool g_workerShouldExit = false;
    HANDLE g_workerThread = nullptr;

    constexpr unsigned kWorkerStackSizeBytes = 8 * 1024 * 1024; // 8MB vs. the ~1MB default

    unsigned __stdcall PresetSwitchWorkerThread(void*)
    {
        for (;;)
        {
            std::string toApply;
            {
                std::unique_lock<std::mutex> lock(g_queueMutex);
                g_queueCv.wait(lock, [] { return g_workerShouldExit || g_pendingPresetPath.has_value(); });

                if (g_workerShouldExit)
                    break;

                toApply = *g_pendingPresetPath;
                g_pendingPresetPath.reset();
            }

            effect_runtime* runtime;
            {
                std::lock_guard<std::mutex> lock(g_runtimeMutex);
                runtime = g_runtime;
            }

            if (runtime == nullptr)
            {
                reshade::log::message(reshade::log::level::warning,
                    "Presetway: preset switch requested but no effect_runtime is available yet; dropping this one.");
                continue;
            }

            reshade::log::message(reshade::log::level::info,
                ("Presetway: switching preset to '" + toApply + "' (worker thread).").c_str());

            runtime->set_current_preset_path(toApply.c_str());

            {
                std::lock_guard<std::mutex> lock(g_queueMutex);
                g_lastAppliedPresetPath = toApply;
            }
        }

        return 0;
    }

    void queue_preset_switch(const std::string& path)
    {
        if (path.empty())
            return;

        {
            std::lock_guard<std::mutex> lock(g_queueMutex);
            if (path == g_lastAppliedPresetPath)
                return;
            g_pendingPresetPath = path;
        }
        g_queueCv.notify_one();
    }

    // Called by Sharingway on its own background thread whenever the "Presetway"
    // provider publishes new data. Expected JSON shape (see the Dalamud plugin):
    //   { "territoryId": ..., "weatherId": ..., "presetPath": "...", "label": "..." }
    void on_sharingway_data(const std::string& provider, const json& data)
    {
        if (provider != "Presetway")
            return;

        if (!data.contains("presetPath") || !data["presetPath"].is_string())
            return;

        const std::string presetPath = data["presetPath"].get<std::string>();
        reshade::log::message(reshade::log::level::info,
            ("Presetway: received preset path '" + presetPath + "'").c_str());

        queue_preset_switch(presetPath);
    }

    void on_provider_status(const std::string& provider, Sharingway::ProviderStatus status)
    {
        if (provider != "Presetway")
            return;

        if (status == Sharingway::ProviderStatus::Online)
        {
            // Guard against subscribing multiple times -- the provider-online
            // notification can fire more than once per session, and stacking up
            // duplicate subscriptions is exactly the kind of thing that erodes
            // stability over a long play session even when it isn't the direct
            // cause of any one crash.
            if (g_subscribedToPresetway.exchange(true))
                return;

            reshade::log::message(reshade::log::level::info, "Presetway: provider online, subscribing.");
            if (g_subscriber != nullptr)
                g_subscriber->SubscribeTo(provider);
        }
        else
        {
            g_subscribedToPresetway.store(false);
        }
    }

    void on_init_effect_runtime(effect_runtime* runtime)
    {
        std::lock_guard<std::mutex> lock(g_runtimeMutex);
        g_runtime = runtime;
    }

    void on_destroy_effect_runtime(effect_runtime* runtime)
    {
        std::lock_guard<std::mutex> lock(g_runtimeMutex);
        if (g_runtime == runtime)
            g_runtime = nullptr;
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD fdwReason, LPVOID)
{
    switch (fdwReason)
    {
    case DLL_PROCESS_ATTACH:
        if (!reshade::register_addon(hModule))
            return FALSE;

        reshade::register_event<reshade::addon_event::init_effect_runtime>(&on_init_effect_runtime);
        reshade::register_event<reshade::addon_event::destroy_effect_runtime>(&on_destroy_effect_runtime);

        g_workerThread = reinterpret_cast<HANDLE>(_beginthreadex(
            nullptr, kWorkerStackSizeBytes, &PresetSwitchWorkerThread, nullptr, 0, nullptr));
        if (g_workerThread == nullptr)
        {
            reshade::log::message(reshade::log::level::error,
                "Presetway addon: failed to start the preset-switch worker thread. Preset switching will not work.");
        }

        g_subscriber = new Sharingway::Subscriber();
        if (g_subscriber->Initialize())
        {
            g_subscriber->SetDataUpdateHandler(&on_sharingway_data);
            g_subscriber->SetProviderChangeHandler(&on_provider_status);

            // Cover both load orders: subscribe immediately to Presetway if it's
            // already online, and auto-subscribe later via the status handler
            // above if the Dalamud plugin loads (or reloads) afterwards.
            for (const auto& providerInfo : g_subscriber->GetAvailableProviders())
            {
                if (providerInfo.name == "Presetway")
                {
                    g_subscriber->SubscribeTo(providerInfo.name);
                    g_subscribedToPresetway.store(true);
                }
            }

            reshade::log::message(reshade::log::level::info, "Presetway addon: Sharingway subscriber ready.");
        }
        else
        {
            reshade::log::message(reshade::log::level::warning,
                "Presetway addon: Sharingway subscriber failed to initialize. Preset switching will not work until this is resolved.");
        }
        break;

    case DLL_PROCESS_DETACH:
        // Bounded wait rather than an unbounded one: waiting on another thread
        // from inside DllMain can deadlock against the loader lock in the worst
        // case, so this gives the worker a couple seconds to exit cleanly and
        // then proceeds regardless rather than risking hanging process shutdown.
        {
            std::lock_guard<std::mutex> lock(g_queueMutex);
            g_workerShouldExit = true;
        }
        g_queueCv.notify_one();
        if (g_workerThread != nullptr)
        {
            WaitForSingleObject(g_workerThread, 2000);
            CloseHandle(g_workerThread);
            g_workerThread = nullptr;
        }

        reshade::unregister_event<reshade::addon_event::init_effect_runtime>(&on_init_effect_runtime);
        reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(&on_destroy_effect_runtime);

        delete g_subscriber;
        g_subscriber = nullptr;

        reshade::unregister_addon(hModule);
        break;
    }

    return TRUE;
}
