/* Razorpay Checkout (cards, UPI apps, net banking). The server verifies the signature before settling. */
(() => {
  const btn = document.querySelector("[data-rzp-key]");
  if (!btn || !window.Razorpay) return;
  btn.addEventListener("click", () => {
    const form = document.querySelector(btn.dataset.rzpForm);
    const rzp = new window.Razorpay({
      key: btn.dataset.rzpKey,
      order_id: btn.dataset.rzpOrder,
      amount: btn.dataset.rzpAmount,
      currency: "INR",
      name: btn.dataset.rzpName,
      notes: { reference: btn.dataset.rzpRef },
      handler: (r) => {
        form.razorpay_order_id.value = r.razorpay_order_id;
        form.razorpay_payment_id.value = r.razorpay_payment_id;
        form.razorpay_signature.value = r.razorpay_signature;
        form.submit();
      },
    });
    rzp.open();
  });
})();
