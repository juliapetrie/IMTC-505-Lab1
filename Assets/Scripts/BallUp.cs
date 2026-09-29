using UnityEngine;

public class BallUp : MonoBehaviour
{
    [SerializeField] private Transform myTransform;
    [SerializeField] private float loopRadius = 2.0f;
    [SerializeField] private float speed = 2.0f;

    private Vector3 centerPosition;

    void Start()
    {
        // If myTransform is not assigned in the Inspector, fall back to this object's Transform
        if (myTransform == null)
        {
            myTransform = transform;
        }

        // Store the starting center point for the loop animation
        centerPosition = myTransform.position;
    }

    void Update()
    {
        float time = Time.time * speed;

        // Calculate circular offsets using sine and cosine
        float offsetX = Mathf.Cos(time) * loopRadius;
        float offsetY = Mathf.Sin(time) * loopRadius;

        // Apply the new position relative to the initial starting point
        myTransform.position = new Vector3(
            centerPosition.x + offsetX,
            centerPosition.y + offsetY,
            centerPosition.z
        );
    }
}