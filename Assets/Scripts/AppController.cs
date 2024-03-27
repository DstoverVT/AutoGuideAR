using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;
using MixedReality.Toolkit.UX;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

/** Controls state of the app, as well as what to display to user. */
public class AppController : MonoBehaviour
{
    private int scanTotalPictureNum = 0;
    //private int totalPicturesToScan = 0;
    //private Queue<Sprite> imageSprites = new Queue<Sprite>();
    /* Holds sprite for each picture in each instruction. 
     * Probably should have used a dictionary similar to storing instruction outputs in JSON on server.
     * So then I wouldn't have to keep everything in order, could label them with instruction num key. */
    private List<List<Sprite>> instructionSprites = new List<List<Sprite>>();
    private bool parsedInstructionOnce = false;
    private DialogPool dialogPool;

    [SerializeField]
    private TextMeshProUGUI instructionLabel;
    [SerializeField]
    private TextMeshPro centerText;
    [SerializeField]
    private TextMeshProUGUI stateText;
    [SerializeField]
    private GameObject debugInfo;
    [SerializeField]
    private Image scanImage;
    [SerializeField]
    private GameObject debugRaycastButton;
    /** Data structure that holds path to images for each instruction as such:
     * [["path1.jpg", "path2.jpg"], ["path3.jpg], ...]
     * This is stored in a Hololens text file as JSON. 
     * Each entry in the outer array corresponds to a different instruction. */
    //private List<List<string>> imagePaths;

    [HideInInspector]
    public int instructionPictureNum = 0;
    public AppState appState { get; set; }
    [HideInInspector]
    public UnityEvent onInstructionsReceived;
    [HideInInspector]
    public UnityEvent onNewInstructionsReceived;
    /* Main components for app. */
    [HideInInspector]
    public InstructionController instructionController;
    [HideInInspector]
    public ObjectDetector objectDetector;
    [HideInInspector]
    public VisualController visualController;
    /** Data structure to hold where to place visuals, example:
     * [
     * [{Vector3: Action 1 GameObject}, {Vector3: Action 2 GameObject}],
     * [{Vector3: Action 3 GameObject}
     * ] 
     * Where each outer list entry corresponds to a different instruction
     */
    public List<List<GameObject>> visualsMap { get; set; } = new List<List<GameObject>>();
    /* Handle updating instructions. */
    [HideInInspector]
    public List<int> updatedInstructions = new List<int>();
    [HideInInspector]
    public bool updateMode = false;

    public enum AppState
    {
        INIT,
        OPERATOR,
        USER_PRESCAN,
        USER
    }


    // Start is called before the first frame update
    void Start()
    {
        ChangeAppState(AppState.INIT);
        instructionController = GetComponent<InstructionController>();
        objectDetector = GetComponent<ObjectDetector>();
        visualController = GetComponent<VisualController>();
        dialogPool = GetComponent<DialogPool>();

        /* Set up keyword speech recognition. */
        var speechRecognition = XRSubsystemHelpers.GetFirstRunningSubsystem<KeywordRecognitionSubsystem>();

        if(speechRecognition != null)
        {
            speechRecognition.CreateOrGetEventForKeyword("debug open").AddListener(() => { debugInfo.SetActive(true); debugRaycastButton.SetActive(true); });
            speechRecognition.CreateOrGetEventForKeyword("debug close").AddListener(() => { debugInfo.SetActive(false); debugRaycastButton.SetActive(false); });
            /* Operator phase voice commands. */
            speechRecognition.CreateOrGetEventForKeyword("operator image").AddListener(HandleOperator);
            speechRecognition.CreateOrGetEventForKeyword("operator done").AddListener(DoneOperator);
            speechRecognition.CreateOrGetEventForKeyword("operator next").AddListener(OperatorNextInstruction);
            speechRecognition.CreateOrGetEventForKeyword("operator previous").AddListener(OperatorPreviousInstruction);
            /* Prescan phase voice commands. */
            speechRecognition.CreateOrGetEventForKeyword("scan next").AddListener(ScanNextPicture);
            speechRecognition.CreateOrGetEventForKeyword("scan previous").AddListener(ScanPreviousPicture);
            speechRecognition.CreateOrGetEventForKeyword("scan image").AddListener(UserPrescan);
            speechRecognition.CreateOrGetEventForKeyword("scan done").AddListener(DonePrescan);
            /* User phase voice commands. */
            speechRecognition.CreateOrGetEventForKeyword("next instruction").AddListener(UserNextInstruction);
            speechRecognition.CreateOrGetEventForKeyword("previous instruction").AddListener(UserPreviousInstruction);
        }

        onInstructionsReceived.AddListener(HandleReceivedInstructions);
        onNewInstructionsReceived.AddListener(HandleReceivedNewInstructions);
    }

    // Update is called once per frame
    void Update()
    {
        /* Display guiding arrow in USER mode. */
        if(appState == AppState.USER)
        {
            /* Set arrow to follow current task's visual. */
            GameObject currentTaskVisual = visualsMap[instructionController.currentInstruction][0];
            visualController.UpdateArrowGuide(currentTaskVisual);
        }
        else
        {
            visualController.ToggleArrow(false);
        }
    }


    /** If using operator mode to change instructions, switch to OPERATOR state. */
    void HandleReceivedNewInstructions()
    {
        DisplayCurrentInstruction();
        /* Reset instruction pictures from last operator phase. */
        if (!updateMode)
        {
            instructionController.ClearStorageFile();
        }
        Debug.Log("OPERATOR state");
        ChangeAppState(AppState.OPERATOR);
    }


    /** If skipping operator mode and using same instructions as before, switch to USER_PRESCAN state. */
    void HandleReceivedInstructions()
    {
        /* Store image paths from previous operator mode instructions. */
        instructionController.LoadImagePathsJson();
        Debug.Log("USER PRESCAN state");
        PrepareUserScan();
        ChangeAppState(AppState.USER_PRESCAN);
    }


    private void ChangeAppState(AppState newState)
    {
        appState = newState;
        string labelText = appState switch
        {
            AppState.INIT => "",
            AppState.OPERATOR => "Operator",
            AppState.USER_PRESCAN => "Scanning",
            AppState.USER => "User",
            _ => "Invalid"
        };

        stateText.SetText(labelText);
    }


    public IEnumerator UpdateCenterText(bool processing, string message)
    {
        if(processing)
        {
            centerText.SetText(message);
            centerText.gameObject.SetActive(true);
        }
        else
        {
            centerText.SetText(message);
            centerText.gameObject.SetActive(true);
            yield return new WaitForSeconds(2);
            /* Disable label after 2 seconds of showing Done */
            centerText.gameObject.SetActive(false);
        }
    }


    public void StartOperator()
    {
        /* Cannot go into operator mode while in scanning phase. */
        if (appState != AppState.USER_PRESCAN)
        {
            /* From https://learn.microsoft.com/en-us/windows/mixed-reality/mrtk-unity/mrtk3-uxcore/packages/uxcore/dialog-api */
            dialogPool.Get()
                .SetHeader("Confirm to Start Operator Mode")
                .SetBody("By clicking confirm you will enter Operator mode, which may overwrite previous instructions that have been saved.")
                .SetPositive("Confirm", (args) => ConfirmStartInstructions(args))
                .SetNegative("Go Back", (args) => {; })
                .Show();
        }
    }


    /** Called from dialog box confirmation. Gets instructions from server. */
            public void ConfirmStartInstructions(DialogButtonEventArgs args)
    {
        /* If coming from USER state, need to update instructions instead of replace them. */
        if (appState == AppState.USER)
        {
            /* Signal that instructions will be updated. */
            HideVisualsMap();
            updateMode = true;
            updatedInstructions.Clear();
            instructionController.StartGetInstructions(true, updateMode);
            scanImage.sprite = null;
        }
        else
        {
            /* If not coming from user mode. */
            instructionController.StartGetInstructions(true, updateMode);
            instructionController.currentInstruction = 0;
        }
    }


    /** Called by voice command "Operator Next" during OPERATOR state. */
    private void OperatorNextInstruction()
    {
        if (appState == AppState.OPERATOR)
        {
            if (instructionController.currentInstruction < (instructionController.instructions.Count - 1))
            {
                instructionController.currentInstruction++;
                DisplayCurrentInstruction();
                parsedInstructionOnce = false;
            }
            /* If gone past last instruction show operator done page. */
            else if(instructionController.currentInstruction == (instructionController.instructions.Count - 1))
            {
                DisplayDoneLabel();
            }
        }
    }


    /** Called by voice command "Operator Previous" during OPERATOR state. */
    private void OperatorPreviousInstruction()
    {
        if (appState == AppState.OPERATOR && 
            instructionController.currentInstruction > 0)
        {
            instructionController.currentInstruction--;
            DisplayCurrentInstruction();
            parsedInstructionOnce = false;
        }
    }


    /** Called by voice command "Operator Image" during OPERATOR state. */
    private void HandleOperator()
    {
        if(appState == AppState.OPERATOR)
        {
            bool isInstructionProcessing = instructionController.instructionProcessing[instructionController.currentInstruction];
            /* For instructions that have multiple images, ensure the last one finished processing. */
            if (parsedInstructionOnce && isInstructionProcessing)
            {
                StartCoroutine(UpdateCenterText(false, "Wait for previous image to process."));
            }
            else
            {
                instructionController.RunInstructionParser();
            }
            parsedInstructionOnce = true;
        }
    }

    /** Operator is complete when they say "Operator Done" during OPERATOR state. */
    private void DoneOperator()
    {
        if (appState == AppState.INIT ||
           appState == AppState.OPERATOR)
        {
            if (appState == AppState.INIT)
            {
                instructionController.StartGetInstructions(false, updateMode);
            }
            else
            {
                /* Save image paths taken during operator mode. */
                instructionController.StoreImagePathsJson();
                Debug.Log("USER PRESCAN state");
                ChangeAppState(AppState.USER_PRESCAN);
                PrepareUserScan();
            }

        }
    }


    private void DisplayCurrentInstruction()
    {
        /* Display current instruction to user. */
        string currInstruction = instructionController.instructions[instructionController.currentInstruction];
        string instructionText = "Instruction " + (instructionController.currentInstruction + 1) + ": " + currInstruction;
        Debug.Log(instructionText);
        instructionLabel.SetText(instructionText);
    }


    private void DisplayDoneLabel()
    {
        string mode = (appState == AppState.OPERATOR) ? "operator" : "scan";
        string label = $"Done with {mode} phase. Say \"{mode} done\" when ready.";
        instructionLabel.SetText(label);
    }


    private void PrepareUserScan()
    {
        /* Only load updated images if updating. */
        if (updateMode)
        {
            StartCoroutine(LoadUpdatedImageTextures());
        }
        /* Load all images if first operator run. */
        else
        {
            StartCoroutine(LoadAllImageTextures());
        }
        instructionController.currentInstruction = 0;
        /* Reset scan indices. */
        instructionPictureNum = 0;
        //totalPicturesToScan = instructionController.GetNumOfInstructionImages();
        scanTotalPictureNum = 0;
        if (updateMode)
        {
            if (updatedInstructions.Count > 0) 
            {
                instructionController.currentInstruction = updatedInstructions[0];
                StartCoroutine(DisplayPicture(instructionController.currentInstruction, instructionPictureNum));
            }
            else
            {
                Debug.Log("Did not update any instructions as expected.");
                /* Skip scanning phase if updated nothing. */
                DonePrescan();
            }
        }
        else
        {
            StartCoroutine(DisplayPicture(instructionController.currentInstruction, instructionPictureNum));
        }
        scanImage.gameObject.SetActive(true);
    }


    /** Returns false if there is not another valid updated instruction after the current one. 
     * Also ensures that the instruction is in the image paths list, so its image can be retrieved.
     */
    private bool GoToNextUpdatedInstruction()
    {
        /* Manage what the next updated instruction is during update mode */
        if (updateMode)
        {
            int currInstructionIdx = updatedInstructions.IndexOf(instructionController.currentInstruction);
            if (currInstructionIdx != -1 && (currInstructionIdx + 1) < updatedInstructions.Count)
            {
                /* Go to updated instruction after the current one. */
                instructionController.currentInstruction = updatedInstructions[currInstructionIdx + 1];
                /* Ensure instruction is in paths list. */
                if (instructionController.currentInstruction >= instructionController.instructionImagePaths.Count)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
        /* Simply advance current instruction by 1 if not in update mode. */
        else
        {
            if ((instructionController.currentInstruction + 1) < instructionController.instructionImagePaths.Count)
            {
                instructionController.currentInstruction++;
            }
            else
            {
                return false;
            }
        }

        return true;
    }


    /** Called by voice command "Scan Next" during USER_PRESCAN state. */
    private void ScanNextPicture()
    {
        if (appState == AppState.USER_PRESCAN)
        {
            List<string> currImages = instructionController.instructionImagePaths[instructionController.currentInstruction];
            /* Try to advance to next picture. */
            /* Advance to next instruction for scanning if done with current one. */
            if ((instructionPictureNum + 1) >= currImages.Count)
            {
                /* Ensure there is another instruction. */
                if (GoToNextUpdatedInstruction())
                {
                    instructionPictureNum = 0;
                    scanTotalPictureNum++;
                    StartCoroutine(DisplayPicture(instructionController.currentInstruction, instructionPictureNum));
                }
                /* Display done when gone through all images. */
                else
                {
                    DisplayDoneLabel();
                    scanImage.gameObject.SetActive(false);
                }
            }
            else
            {
                instructionPictureNum++;
                scanTotalPictureNum++;
                StartCoroutine(DisplayPicture(instructionController.currentInstruction, instructionPictureNum));
            }
        }
    }



    /** Called by voice command "Scan Previous" during USER_PRESCAN state. */
    private void ScanPreviousPicture()
    {
        if (appState == AppState.USER_PRESCAN)
        {
            /* Need to go to last picture of last instruction. */
            if(instructionPictureNum == 0 && instructionController.currentInstruction > 0)
            {
                instructionController.currentInstruction--;
                List<string> lastImages = instructionController.instructionImagePaths[instructionController.currentInstruction];
                instructionPictureNum = lastImages.Count - 1;
                scanTotalPictureNum--;
                StartCoroutine(DisplayPicture(instructionController.currentInstruction, instructionPictureNum));
            }
            /* Go to last picture of current instruction. */
            else if(instructionPictureNum > 0)
            {
                instructionPictureNum--;
                scanTotalPictureNum--;
                StartCoroutine(DisplayPicture(instructionController.currentInstruction, instructionPictureNum));
            }
        }
    }

    
    /** Called by voice command "Scan Image" in USER_PRESCAN state. */
    private void UserPrescan()
    {
        if (appState == AppState.USER_PRESCAN)
        {
            objectDetector.RunObjectDetector();
        }
    }


    /** Called by voice command "Scan Done" in USER_PRESCAN state. */
    private void DonePrescan()
    {
       if(appState == AppState.USER_PRESCAN)
        {
            instructionController.currentInstruction = 0;
            Debug.Log("USER state");
            ChangeAppState(AppState.USER);
            scanImage.gameObject.SetActive(false);
            /* Display guidance for first instruction. */
            DisplayTaskGuidance();
        }
    }


    /** Coroutine that pushes the front of the Texture Queue as the current image to display.
     * If the queue is empty, wait for an element to be pushed. */
    IEnumerator DisplayPicture(int currentInstruction, int currentPicture)
    {
        /* Wait for current instruction picture to have a valid sprite. */
        yield return new WaitUntil(() => (currentInstruction < instructionSprites.Count) &&
                                         (currentPicture < instructionSprites[currentInstruction].Count));
        /* Get texture for current image. */
        //string currImagePath = instructionController.instructionImagePaths[instructionController.currentInstruction][instructionPictureNum];
        ///* Read file into Texture */
        //byte[] imageBytes = File.ReadAllBytes(currImagePath);
        //Texture2D tex = new Texture2D(2, 2);
        //tex.LoadImage(imageBytes);

        ///* Create sprite from texture and set in GameObject to display. */
        //Sprite imageSprite = Sprite.Create(tex, new Rect(0.0f, 0.0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100.0f);

        scanImage.sprite = instructionSprites[currentInstruction][currentPicture];
        /* Set text with image number. */
        instructionLabel.SetText($"Scan Image {scanTotalPictureNum + 1}:");
    }


    /** Load all images from image paths. Creates new instructionSprites list. */
    IEnumerator LoadAllImageTextures()
    {
        ///* Create picture lists for all instructions and pictures if don't exist */
        //if (instructionController.instructionImagePaths.Count > instructionSprites.Count)
        //{
        //    int numToAdd = instructionController.instructionImagePaths.Count - instructionSprites.Count;
        //    for (int i = 0; i < numToAdd; i++)
        //    {
        //        instructionSprites.Add(new List<Sprite>());
        //    }
        //}
        //for(int i = 0; i < instructionController.instructionImagePaths.Count; i++)
        //{
        //    int numPictures = instructionController.instructionImagePaths
        //}

        instructionSprites.Clear();

        StartCoroutine(UpdateCenterText(true, "Loading images, please wait."));
        /* Convert all images to textures and store them in Queue. */
        for (int instruction = 0; instruction < instructionController.instructionImagePaths.Count; instruction++)
        {
            for(int picture = 0; picture < instructionController.instructionImagePaths[instruction].Count; picture++)
            {
                instructionSprites.Add(new List<Sprite>());
                string path = instructionController.instructionImagePaths[instruction][picture];
                string fileURI = "file:///" + path;
                /* Use Unity's GetTexture async operation. */
                using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(fileURI))
                {
                    yield return request.SendWebRequest();

                    if(request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"Could not convert an image to texture: {path}");
                    }
                    else
                    {
                        Texture2D tex = DownloadHandlerTexture.GetContent(request);
                        Sprite sprite = Sprite.Create(tex, new Rect(0.0f, 0.0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100.0f);
                        instructionSprites[instruction].Add(sprite);
                    }
                }
            }
        }
        StartCoroutine(UpdateCenterText(false, "Done"));
    }


    IEnumerator LoadUpdatedImageTextures()
    {
        StartCoroutine(UpdateCenterText(true, "Loading images, please wait."));

        foreach(int instructionNum in updatedInstructions)
        {
            /* Clear old pictures from updated instruction. */
            instructionSprites[instructionNum].Clear();
            foreach (string path in instructionController.instructionImagePaths[instructionNum])
            {
                string fileURI = "file:///" + path;
                /* Use Unity's GetTexture async operation. */
                using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(fileURI))
                {
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"Could not convert an image to texture: {path}");
                    }
                    else
                    {
                        Texture2D tex = DownloadHandlerTexture.GetContent(request);
                        Sprite sprite = Sprite.Create(tex, new Rect(0.0f, 0.0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100.0f);
                        instructionSprites[instructionNum].Add(sprite);
                    }
                }
            }
        }
        StartCoroutine(UpdateCenterText(false, "Done"));
    }


    /** Called by voice command "Next Instruction" in USER state.
     * Advance to next instruction for task guidance. */
    private void UserNextInstruction()
    {
        if (appState == AppState.USER &&
           instructionController.currentInstruction < (instructionController.instructions.Count - 1))
        {
            instructionController.currentInstruction++;
            DisplayTaskGuidance();
        }
    }


    /** Called by voice command "Previous Instruction" in USER state.
     * Advance to next instruction for task guidance. */
    private void UserPreviousInstruction()
    {
        if (appState == AppState.USER &&
           instructionController.currentInstruction > 0)
        {
            instructionController.currentInstruction--;
            DisplayTaskGuidance();
        }
    }


private void DisplayTaskGuidance()
    {
        DisplayCurrentInstruction();
        visualController.ToggleArrow(true);
        /* Display current instruction visuals. */
        for(int i = 0; i < visualsMap.Count; i++)
        {
            List<GameObject> instructionVisuals = visualsMap[i];
            /* Only display visuals for current instruction. */
            bool displayVisual = false;
            if(i == instructionController.currentInstruction)
            {
                /* No visuals found for this instruction. */
                if(instructionVisuals.Count == 0)
                {
                    visualController.ToggleArrow(false);
                    StartCoroutine(UpdateCenterText(false, "No visual found for this instruction."));
                }
                displayVisual = true;
            }
            /* Set current instructions visuals active, and hide all others. */
            foreach(GameObject visual in instructionVisuals)
            {
                visual.SetActive(displayVisual);
            }
        }
    }


    /** Clear visuals and set all gameobjects inactive. */
    private void HideVisualsMap()
    {
        foreach(List<GameObject> instructionVisuals in visualsMap)
        {
            foreach(GameObject visual in instructionVisuals)
            {
                /* Set visuals inactive and get rid of them. */
                visual.SetActive(false);
            }
        }
    }
}
