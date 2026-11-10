#if USE_HELPER
__declspec(dllimport) int Helper(int value);
__declspec(dllimport) int OrdinalHelper(int value);
#endif
#if USE_HELPER == 2
__declspec(dllimport) int NewHelper(int value);
#endif

int Add(int left, int right)
{
#if USE_HELPER == 2
    return Helper(left) + NewHelper(right) + OrdinalHelper(0);
#elif USE_HELPER == 1
    return Helper(left) + right + OrdinalHelper(0);
#else
    return left + right;
#endif
}

int Subtract(int left, int right) { return left - right; }
int Multiply(int left, int right) { return left * right; }
int Hidden(int value) { return value; }

#if DELAY_STUB
/* Link-only test fixture. This is not a functional delay-load helper. */
void *__stdcall __delayLoadHelper2(void *descriptor, void *thunk)
{
    (void)descriptor;
    (void)thunk;
    return 0;
}
#endif
