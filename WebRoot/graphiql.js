// import * as session from "./sessions"
// import getConfig from "./config"

// let token = session.getToken()

// if (token == null) throw new Error("Couldn't find token")

function graphQLFetcher(graphQLParams) {
	// let config = getConfig()
	let apiHost = "/"

	return fetch(apiHost + "/graphql-app", {
		method: "post",
		headers: {
			"Content-Type": "application/json",
			// "Authorization": "Bearer " + token,
		},
		body: JSON.stringify(graphQLParams),
	}).then(response => response.json());

}

let node = document.getElementById("app")

ReactDOM.render(
	React.createElement(GraphiQL, { fetcher: graphQLFetcher }),
	node,
)
