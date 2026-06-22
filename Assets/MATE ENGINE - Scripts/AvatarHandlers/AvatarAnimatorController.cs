using UnityEngine;
using NAudio.CoreAudioApi;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections;

public class AvatarAnimatorController : MonoBehaviour
{
    [Header("State Values")]
    public Animator animator;
    public float SOUND_THRESHOLD = 0.02f;
    public List<string> allowedApps = new();
    public int totalIdleAnimations = 10;
    public float IDLE_SWITCH_TIME = 12f, IDLE_TRANSITION_TIME = 3f;
    public int DANCE_CLIP_COUNT = 5;

    [Header("Dancing")]
    public bool enableDancing = true;           
    public bool enableDanceSwitch = true;
    public float DANCE_SWITCH_TIME = 15f;
    public float DANCE_TRANSITION_TIME = 2f;       

    public bool BlockDraggingOverride = false;

    private static readonly int danceIndexParam = Animator.StringToHash("DanceIndex");
    private static readonly int isIdleParam = Animator.StringToHash("isIdle");
    private static readonly int isDraggingParam = Animator.StringToHash("isDragging");
    private static readonly int isDancingParam = Animator.StringToHash("isDancing");
    private static readonly int idleIndexParam = Animator.StringToHash("IdleIndex");

    private MMDevice defaultDevice;
    private MMDeviceEnumerator enumerator;
    private Coroutine soundCheckCoroutine, idleTransitionCoroutine, danceTransitionCoroutine;
    private float lastSoundCheckTime, idleTimer, danceTimer;
    private int idleState, danceState;
    private float dragLockTimer;
    private bool mouseHeld;
    public bool isDragging, isDancing, isIdle;

    [Header("Character Mode")]
    public bool enableHusbandoMode = false;
    private static readonly int isMaleParam = Animator.StringToHash("isMale");
    private static readonly int isFemaleParam = Animator.StringToHash("isFemale");

    [Header("BPM Sync")]
    private AudioSessionControl activeAudioSession;
    private List<float> bpmHistory = new List<float>();
    private float lastBeatTime = 0f;
    private float dynamicBeatThreshold = 0.05f;
    private float currentEstimatedBPM = 120f;
    private float lastValidSoundTime = 0f;
    private float targetAnimatorSpeed = 1f;
    private float smoothedPeak = 0f;


    void OnEnable()
    {
        animator ??= GetComponent<Animator>();
        Application.runInBackground = true;
        enumerator = new MMDeviceEnumerator();
        defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        animator.SetFloat(isFemaleParam, enableHusbandoMode ? 0f : 1f);
        animator.SetFloat(isMaleParam, enableHusbandoMode ? 1f : 0f);

        soundCheckCoroutine = StartCoroutine(CheckSoundContinuously());
    }

    void OnDisable() => CleanupAudioResources();
    void OnDestroy() => CleanupAudioResources();
    void OnApplicationQuit() => CleanupAudioResources();

    IEnumerator CheckSoundContinuously()
    {
        var wait = new WaitForSeconds(2f);
        while (true) { CheckForSound(); yield return wait; }
    }

    void CheckForSound()
    {
        if (MenuActions.IsMovementBlocked() || !enableDancing)
        {
            if (isDancing) SetDancing(false);
            return;
        }
        if (defaultDevice == null) return;
        if (!isDragging)
        {
            bool valid = IsValidAppPlaying();
            if (valid && !isDancing) StartDancing();
            else if (!valid && isDancing) SetDancing(false);
        }
    }

    void StartDancing()
    {
        isDancing = true;
        danceTimer = 0f;
        danceState = Random.Range(0, DANCE_CLIP_COUNT);
        animator.SetBool(isDancingParam, true);
        animator.SetFloat(danceIndexParam, danceState);
    }
    void SetDancing(bool value)
    {
        isDancing = value;
        animator.SetBool(isDancingParam, value);
        if (!value)
        {
            animator.speed = 1f; // Reset speed when not dancing
            targetAnimatorSpeed = 1f;
            smoothedPeak = 0f;
            bpmHistory.Clear();
            activeAudioSession = null;
            if (danceTransitionCoroutine != null)
            {
                StopCoroutine(danceTransitionCoroutine);
                danceTransitionCoroutine = null;
            }
        }
    }

    bool IsValidAppPlaying()
    {
        if (Time.time - lastSoundCheckTime < 2f) return isDancing;
        lastSoundCheckTime = Time.time;
        try
        {
            defaultDevice?.Dispose();
            defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0, count = sessions.Count; i < count; i++)
            {
                var s = sessions[i];
                if (s.AudioMeterInformation.MasterPeakValue > SOUND_THRESHOLD)
                {
                    int pid = (int)s.GetProcessID;
                    if (pid == 0) continue;
                    try
                    {
                        string pname = Process.GetProcessById(pid)?.ProcessName;
                        if (string.IsNullOrEmpty(pname)) continue;
                        for (int j = 0; j < allowedApps.Count; j++)
                            if (pname.StartsWith(allowedApps[j], System.StringComparison.OrdinalIgnoreCase))
                            {
                                activeAudioSession = s; // Track the session
                                return true;
                            }
                    }
                    catch { continue; }
                }
            }
        }
        catch { defaultDevice?.Dispose(); defaultDevice = null; }
        
        // If we are currently dancing, and heard a peak recently, ignore this silence.
        if (isDancing && Time.time - lastValidSoundTime < 1.5f) {
            return true;
        }

        activeAudioSession = null;
        return false;
    }

    void Update()
    {
        animator.SetFloat(isFemaleParam, enableHusbandoMode ? 0f : 1f);
        animator.SetFloat(isMaleParam, enableHusbandoMode ? 1f : 0f);

        if (BlockDraggingOverride || MenuActions.IsMovementBlocked() || TutorialMenu.IsActive)
        {
            if (isDragging) SetDragging(false);
            if (isDancing) SetDancing(false);
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            SetDragging(true);
            mouseHeld = true;
            dragLockTimer = 0.30f;
            SetDancing(false);
        }
        if (Input.GetMouseButtonUp(0)) mouseHeld = false;
        if (dragLockTimer > 0f)
        {
            dragLockTimer -= Time.deltaTime;
            animator.SetBool(isDraggingParam, true);
        }
        else if (!mouseHeld && isDragging) SetDragging(false);

        idleTimer += Time.deltaTime;
        if (idleTimer > IDLE_SWITCH_TIME)
        {
            idleTimer = 0f;
            int next = (idleState + 1) % totalIdleAnimations;
            if (next == 0) animator.SetFloat(idleIndexParam, 0);
            else
            {
                if (idleTransitionCoroutine != null) StopCoroutine(idleTransitionCoroutine);
                idleTransitionCoroutine = StartCoroutine(SmoothIdleTransition(next));
            }
            idleState = next;
        }
        UpdateIdleStatus();

        if (isDancing)
        {
            ProcessBpmSync();
        }

        if (isDancing && enableDanceSwitch)
        {
            danceTimer += Time.deltaTime;
            if (danceTimer > DANCE_SWITCH_TIME)
            {
                danceTimer = 0f;
                int nextDance = (danceState + 1) % DANCE_CLIP_COUNT;
                if (nextDance == 0) animator.SetFloat(danceIndexParam, 0);
                else
                {
                    if (danceTransitionCoroutine != null) StopCoroutine(danceTransitionCoroutine);
                    danceTransitionCoroutine = StartCoroutine(SmoothDanceTransition(nextDance));
                }
                danceState = nextDance;
            }
        }
    }
    void SetDragging(bool value)
    {
        isDragging = value;
        animator.SetBool(isDraggingParam, value);
    }

    void ProcessBpmSync()
    {
        if (activeAudioSession == null) return;
        try
        {
            // Smart BPM Reset
            if (Time.time - lastBeatTime > 3f)
            {
                bpmHistory.Clear();
            }

            float peak = activeAudioSession.AudioMeterInformation.MasterPeakValue;
            if (peak > SOUND_THRESHOLD) lastValidSoundTime = Time.time;
            
            // Energy Envelope & Faster Threshold Decay
            smoothedPeak = Mathf.Lerp(smoothedPeak, peak, Time.deltaTime * (peak > smoothedPeak ? 15f : 3f));
            dynamicBeatThreshold = Mathf.Lerp(dynamicBeatThreshold, SOUND_THRESHOLD, Time.deltaTime * 1.5f);
            
            if (peak > dynamicBeatThreshold && peak > smoothedPeak * 1.35f && peak > SOUND_THRESHOLD * 1.5f)
            {
                float timeSinceLastBeat = Time.time - lastBeatTime;
                
                float minimumWaitTime = 0.25f;
                // Predictive Metronome: If we have a stable tempo, block noises that fall outside the beat window
                if (bpmHistory.Count >= 8) 
                {
                    float expectedInterval = 60f / currentEstimatedBPM;
                    // Prevent any triggers until we are at least 85% of the way to the expected beat
                    minimumWaitTime = expectedInterval * 0.85f; 
                }

                // Expand range to catch variations, but use logic to normalize
                if (timeSinceLastBeat > minimumWaitTime && timeSinceLastBeat < 1.5f)
                {
                    float instantaneousBPM = 60f / timeSinceLastBeat;

                    bpmHistory.Add(instantaneousBPM);
                    if (bpmHistory.Count > 11) bpmHistory.RemoveAt(0); // keep last 11 beats

                    // Outlier Rejection (Median Filter)
                    if (bpmHistory.Count > 0)
                    {
                        List<float> sortedBpm = new List<float>(bpmHistory);
                        sortedBpm.Sort();
                        currentEstimatedBPM = sortedBpm[sortedBpm.Count / 2];
                    }

                    float finalBPM = currentEstimatedBPM;

                    // If the stable median BPM is very fast (> 150), we half-time it 
                    // so the avatar doesn't look frantic.
                    if (finalBPM > 150f)
                    {
                        finalBPM /= 2f;
                    }
                    // If the BPM is very slow (< 75), we double-time it 
                    // so the avatar doesn't look like it's in slow-motion.
                    else if (finalBPM < 75f)
                    {
                        finalBPM *= 2f;
                    }

                    // Update target speed (assuming default dance animations are authored for 120 BPM)
                    targetAnimatorSpeed = Mathf.Clamp(finalBPM / 120f, 0.5f, 1.5f);
                }
                
                if (timeSinceLastBeat > minimumWaitTime) // Prevent rapid double-triggering
                {
                    lastBeatTime = Time.time;
                    dynamicBeatThreshold = peak; // Jump threshold to current peak
                }
            }

            // Smooth Animation Transition
            animator.speed = Mathf.Lerp(animator.speed, targetAnimatorSpeed, Time.deltaTime * 2f);
        }
        catch 
        { 
            // In case session becomes invalid
            activeAudioSession = null; 
        }
    }

    void UpdateIdleStatus()
    {
        bool inIdle = animator.GetCurrentAnimatorStateInfo(0).IsName("Idle");
        if (isIdle != inIdle)
        {
            isIdle = inIdle;
            animator.SetBool(isIdleParam, isIdle);
        }
    }

    IEnumerator SmoothIdleTransition(int newIdle)
    {
        float elapsed = 0f, start = animator.GetFloat(idleIndexParam);
        while (elapsed < IDLE_TRANSITION_TIME)
        {
            elapsed += Time.deltaTime;
            animator.SetFloat(idleIndexParam, Mathf.Lerp(start, newIdle, elapsed / IDLE_TRANSITION_TIME));
            yield return null;
        }
        animator.SetFloat(idleIndexParam, newIdle);
    }

    IEnumerator SmoothDanceTransition(int newDance)
    {
        float elapsed = 0f, start = animator.GetFloat(danceIndexParam);
        while (elapsed < DANCE_TRANSITION_TIME)
        {
            elapsed += Time.deltaTime;
            animator.SetFloat(danceIndexParam, Mathf.Lerp(start, newDance, elapsed / DANCE_TRANSITION_TIME));
            yield return null;
        }
        animator.SetFloat(danceIndexParam, newDance);
    }

    public bool IsInIdleState() => isIdle;

    void CleanupAudioResources()
    {
        if (soundCheckCoroutine != null) { StopCoroutine(soundCheckCoroutine); soundCheckCoroutine = null; }
        if (idleTransitionCoroutine != null) { StopCoroutine(idleTransitionCoroutine); idleTransitionCoroutine = null; }
        if (danceTransitionCoroutine != null) { StopCoroutine(danceTransitionCoroutine); danceTransitionCoroutine = null; }
        defaultDevice?.Dispose(); defaultDevice = null;
        enumerator?.Dispose(); enumerator = null;
    }
}
