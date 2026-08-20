// Presettingway ReShade addon
//
// Subscribes to the "Presettingway" Sharingway provider (published by the Presettingway
// Dalamud plugin) and calls effect_runtime::set_current_preset_path() whenever
// a new preset path arrives.
//
// ARCHITECTURE HISTORY -- three attempts, two proven wrong with evidence:
//
// v1: applied the switch from inside the reshade_present addon-event callback.
//     Crashed reliably and reproducibly with STATUS_STACK_OVERFLOW (0xc00000fd),
//     confirmed via Windows Event Viewer across multiple independent sessions.
//
// v2: moved the switch to a dedicated worker thread instead, to get outside
//     ReShade's event-dispatch call stack entirely. Fixed the stack overflow,
//     but introduced a different problem with heavier presets: an
//     ACCESS_VIOLATION (0xC0000005) crash, confirmed via a full crash dump,
//     with the fault occurring inside REST (ReshadeEffectShaderToggler) and
//     dxgi.dll on the render thread -- consistent with a cross-thread race
//     between our worker thread mutating shared ReShade/effect_runtime state
//     and REST reading/writing that same state during its own per-frame work.
//     A heavier preset means more concurrent render-thread work from REST,
//     which plausibly explains why the odds of hitting the race scaled with
//     preset weight.
//
// v3 (this version): neither "inside reshade_present" nor "an uncoordinated
//     background thread" is actually safe. Confirmed by reading ReShade's own
//     source (source/runtime.cpp, source/dxgi/dxgi_swapchain.cpp):
//       - runtime::on_present() sets _is_in_present_call = true at its start,
//         does ALL of ReShade's own per-frame effect/overlay rendering and
//         command-list work, fires reshade_present near the very end, and
//         only then sets the flag back to false. Calling into effect_runtime
//         (which triggers a full effect reload -- shader recompilation,
//         resource creation/destruction) from reshade_present therefore
//         happens while ReShade's own command list for the frame may still
//         be open and in active use.
//       - addon_event::present, by contrast, is invoked in
//         DXGISwapChain::on_present() *before* present_effect_runtime() (and
//         therefore before runtime::on_present()) is even called. It fires on
//         the correct thread, but genuinely outside ReShade's own nested
//         per-frame processing -- before _is_in_present_call is set, before
//         any of ReShade's own command-list work for the frame has started.
//     addon_event::present hands us a swapchain*/command_queue*, not an
//     effect_runtime* -- effect_runtime and swapchain are sibling interfaces
//     in the addon API (both inherit device_object independently), not
//     parent/child, so there's no cast between them. We don't need one: we
//     already track the effect_runtime* ourselves via init_effect_runtime,
//     and addon_event::present is used purely as a timing signal ("this
//     moment is safe"), with its own parameters ignored entirely.
//
// No worker thread needed any more -- back to a single per-frame check, but
// at a point in the frame that's actually outside ReShade's own danger zone,
// rather than deep inside it.

#include <reshade.hpp>
#include <atomic>
#include <mutex>
#include <string>
#include <optional>

#include "ThirdParty/Sharingway/Sharingway.h"

using namespace reshade::api;

namespace
{
    Sharingway::Subscriber* g_subscriber = nullptr;
    std::atomic<bool> g_subscribedToPresettingway{ false };

    // The currently-valid effect_runtime, captured/cleared via the two
    // lifecycle events below. addon_event::present doesn't hand us one
    // directly (see the architecture note above), so we track it ourselves.
    std::mutex g_runtimeMutex;
    effect_runtime* g_runtime = nullptr;

    std::mutex g_queueMutex;
    std::optional<std::string> g_pendingPresetPath;
    std::string g_lastAppliedPresetPath;

    void queue_preset_switch(const std::string& path)
    {
        if (path.empty())
            return;

        std::lock_guard<std::mutex> lock(g_queueMutex);
        if (path != g_lastAppliedPresetPath)
            g_pendingPresetPath = path;
    }

    // Called by Sharingway on its own background thread whenever the "Presettingway"
    // provider publishes new data. Expected JSON shape (see the Dalamud plugin):
    //   { "territoryId": ..., "weatherId": ..., "presetPath": "...", "label": "..." }
    void on_sharingway_data(const std::string& provider, const json& data)
    {
        if (provider != "Presettingway")
            return;

        if (!data.contains("presetPath") || !data["presetPath"].is_string())
            return;

        const std::string presetPath = data["presetPath"].get<std::string>();
        reshade::log::message(reshade::log::level::info,
            ("Presettingway: received preset path '" + presetPath + "'").c_str());

        queue_preset_switch(presetPath);
    }

    void on_provider_status(const std::string& provider, Sharingway::ProviderStatus status)
    {
        if (provider != "Presettingway")
            return;

        if (status == Sharingway::ProviderStatus::Online)
        {
            // Guard against subscribing multiple times -- the provider-online
            // notification can fire more than once per session, and stacking up
            // duplicate subscriptions is exactly the kind of thing that erodes
            // stability over a long play session even when it isn't the direct
            // cause of any one crash.
            if (g_subscribedToPresettingway.exchange(true))
                return;

            reshade::log::message(reshade::log::level::info, "Presettingway: provider online, subscribing.");
            if (g_subscriber != nullptr)
                g_subscriber->SubscribeTo(provider);
        }
        else
        {
            g_subscribedToPresettingway.store(false);
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

    // Fires once per frame, on the render thread, BEFORE ReShade's own
    // present_effect_runtime()/runtime::on_present() is called at all --
    // confirmed via ReShade's actual source, not inferred. This is used purely
    // as a safe timing checkpoint; all of its own parameters are ignored.
    void on_present(command_queue*, swapchain*, const rect*, const rect*, uint32_t, const rect*)
    {
        std::optional<std::string> toApply;
        {
            std::lock_guard<std::mutex> lock(g_queueMutex);
            if (g_pendingPresetPath.has_value())
            {
                toApply = g_pendingPresetPath;
                g_pendingPresetPath.reset();
            }
        }

        if (!toApply.has_value())
            return;

        effect_runtime* runtime;
        {
            std::lock_guard<std::mutex> lock(g_runtimeMutex);
            runtime = g_runtime;
        }

        if (runtime == nullptr)
        {
            reshade::log::message(reshade::log::level::warning,
                "Presettingway: preset switch requested but no effect_runtime is available yet; dropping this one.");
            return;
        }

        reshade::log::message(reshade::log::level::info,
            ("Presettingway: switching preset to '" + *toApply + "'").c_str());

        runtime->set_current_preset_path(toApply->c_str());
        g_lastAppliedPresetPath = *toApply;
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
        reshade::register_event<reshade::addon_event::present>(&on_present);

        g_subscriber = new Sharingway::Subscriber();
        if (g_subscriber->Initialize())
        {
            g_subscriber->SetDataUpdateHandler(&on_sharingway_data);
            g_subscriber->SetProviderChangeHandler(&on_provider_status);

            // Cover both load orders: subscribe immediately to Presettingway if it's
            // already online, and auto-subscribe later via the status handler
            // above if the Dalamud plugin loads (or reloads) afterwards.
            for (const auto& providerInfo : g_subscriber->GetAvailableProviders())
            {
                if (providerInfo.name == "Presettingway")
                {
                    g_subscriber->SubscribeTo(providerInfo.name);
                    g_subscribedToPresettingway.store(true);
                }
            }

            reshade::log::message(reshade::log::level::info, "Presettingway addon: Sharingway subscriber ready.");
        }
        else
        {
            reshade::log::message(reshade::log::level::warning,
                "Presettingway addon: Sharingway subscriber failed to initialize. Preset switching will not work until this is resolved.");
        }
        break;

    case DLL_PROCESS_DETACH:
        reshade::unregister_event<reshade::addon_event::init_effect_runtime>(&on_init_effect_runtime);
        reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(&on_destroy_effect_runtime);
        reshade::unregister_event<reshade::addon_event::present>(&on_present);

        delete g_subscriber;
        g_subscriber = nullptr;

        reshade::unregister_addon(hModule);
        break;
    }

    return TRUE;
}
