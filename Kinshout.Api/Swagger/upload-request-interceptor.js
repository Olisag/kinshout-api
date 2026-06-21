function (req) {
  var url = (req.url || "").toLowerCase();
  if (url.indexOf("/api/uploads/") === -1) {
    return req;
  }

  var clientToken =
    (req.headers && (req.headers["X-Kinshout-Client-Token"] || req.headers["x-kinshout-client-token"])) ||
    null;

  if (!clientToken && window.ui) {
    try {
      var auth = window.ui.getState().get("auth").get("authorized");
      if (auth && auth.ClientToken && auth.ClientToken.value) {
        clientToken = auth.ClientToken.value;
      } else if (auth && auth.toJS) {
        var plain = auth.toJS();
        if (plain.ClientToken && plain.ClientToken.value) {
          clientToken = plain.ClientToken.value;
        }
      }
    } catch (e) { /* ignore */ }
  }

  if (clientToken && req.body instanceof FormData) {
    req.body.delete("x_kinshout_client_token");
    req.body.append("x_kinshout_client_token", clientToken);
  }

  if (req.headers) {
    var ct = req.headers["Content-Type"] || req.headers["content-type"];
    if (ct === "multipart/form-data") {
      delete req.headers["Content-Type"];
      delete req.headers["content-type"];
    }
  }

  return req;
}
